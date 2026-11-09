"""Critical Count's lounge music: a menu loop and a table loop (2026-11-08).

Both loops are 24 bars of 4/4 at 96 BPM = exactly 60 s, in F major, on the same chart, so the
game can crossfade between them at the same playback position without a seam.

They are locked to the board's live layer (board_fx.gdshaderinc): a ring leaves the emblem every
5 s = every 2 bars, so every second downbeat gets an accent (bass root, a soft kick at the table,
a chord attack in the menu). The game drives the board's clock from the music's position.

Written as MIDI with mido, rendered with FluidSynth and the FluidR3 General MIDI soundfont, and
encoded to Ogg Vorbis with ffmpeg. Each loop is rendered three times over and the middle
repetition is kept, so the reverb tail from the end of the loop is already under its start.

    python tools/musicgen/musicgen.py <out_dir> [soundfont.sf2]
"""
import random
import subprocess
import sys
import tempfile
from pathlib import Path

import mido
import numpy as np
import scipy.io.wavfile as wavfile

BPM = 96
TPB = 480                      # ticks per beat
BAR = 4 * TPB
BARS = 24                      # 24 bars at 96 BPM = 60.0 s
SWING = 320                    # a swung off-beat lands 2/3 of the way through the beat
SR = 44100
LOOP_SECONDS = BARS * 4 * 60 / BPM

NOTE = {"C": 0, "Db": 1, "D": 2, "Eb": 3, "E": 4, "F": 5, "F#": 6, "Gb": 6, "G": 7, "Ab": 8,
        "A": 9, "Bb": 10, "B": 11}


def n(name):
    """'A4' -> 69."""
    pitch, octave = name[:-1], int(name[-1])
    return 12 * (octave + 1) + NOTE[pitch]


# The chart: one or two chords a bar (root, quality).
CHART = [
    # A
    [("F", "maj7")], [("D", "7")], [("G", "m7")], [("C", "7")],
    [("A", "m7")], [("D", "7")], [("G", "m7"), ("C", "7")], [("F", "maj7")],
    # B
    [("C", "m7"), ("F", "7")], [("Bb", "maj7")], [("Bb", "m7"), ("Eb", "7")], [("A", "m7")],
    [("D", "7")], [("G", "m7")], [("G", "m7"), ("C", "7")], [("C", "7")],
    # A'
    [("F", "maj7")], [("D", "7")], [("G", "m7")], [("C", "7")],
    [("A", "m7"), ("D", "7")], [("G", "m7"), ("C", "7")], [("F", "maj7")], [("G", "m7"), ("C", "7")],
]
assert len(CHART) == BARS

QUALITY = {  # chord tones above the root: 3rd, 5th, 7th, 9th
    "maj7": (4, 7, 11, 14),
    "7": (4, 7, 10, 14),
    "m7": (3, 7, 10, 14),
}

# The tune (vibraphone): per bar, (beat, length in beats, note).
A_TUNE = [
    [(0.5, 0.5, "A4"), (1, 0.5, "C5"), (1.5, 0.5, "E5"), (2, 1.5, "D5"), (3.5, 0.5, "C5")],
    [(0, 2, "A4"), (2.5, 0.5, "F#4"), (3, 0.5, "A4"), (3.5, 0.5, "C5")],
    [(0, 1.5, "Bb4"), (1.5, 0.5, "A4"), (2, 0.5, "G4"), (2.5, 0.5, "F4"), (3, 1, "D5")],
    [(0, 2, "E5"), (2, 0.5, "Bb4"), (2.5, 1.5, "G4")],
    [(0.5, 0.5, "E5"), (1, 0.5, "G5"), (1.5, 0.5, "E5"), (2, 2, "C5")],
    [(0, 0.5, "F#5"), (0.5, 0.5, "E5"), (1, 1, "D5"), (2, 1, "C5"), (3, 1, "A4")],
    [(0, 1, "Bb4"), (1, 1, "D5"), (2, 1, "E5"), (3, 1, "Bb4")],
    [(0, 3, "A4")],
]
B_TUNE = [
    [(0, 1, "Eb5"), (1, 0.5, "D5"), (1.5, 0.5, "C5"), (2, 1, "A4"), (3, 1, "C5")],
    [(0, 3, "D5"), (3.5, 0.5, "F5")],
    [(0, 1, "Db5"), (1, 0.5, "C5"), (1.5, 0.5, "Bb4"), (2, 1, "G4"), (3, 1, "Db5")],
    [(0, 3, "C5"), (3.5, 0.5, "E5")],
    [(0, 1, "F#5"), (1, 1, "A5"), (2, 1, "F#5"), (3, 1, "D5")],
    [(0, 2, "F5"), (2, 0.5, "D5"), (2.5, 0.5, "Bb4"), (3, 1, "A4")],
    [(0, 1, "G4"), (1, 1, "Bb4"), (2, 1, "D5"), (3, 1, "E5")],
    [(0, 2, "G5"), (2.5, 0.5, "E5"), (3, 1, "Bb4")],
]
A2_TUNE = A_TUNE[:6] + [
    [(0, 2, "F5"), (2, 1, "E5"), (3, 1, "C5")],
    [(0, 1, "D5"), (1, 1, "Bb4"), (2, 1, "G4"), (3, 0.5, "E4"), (3.5, 0.5, "G4")],
]
TUNE = A_TUNE + B_TUNE + A2_TUNE
assert len(TUNE) == BARS

GM = {"piano": 0, "rhodes": 4, "vibes": 11, "bass": 32, "pad": 89, "strings": 49}
BRUSH_KIT = 40
KICK, SNARE_SWISH, SNARE, RIDE, RIDE_BELL, HAT_PEDAL = 36, 40, 38, 51, 53, 44


class Track:
    def __init__(self, channel, program, volume=100, pan=64, bank=0):
        self.ch = channel
        self.events = []  # (tick, order, message)
        self.events.append((0, 0, mido.Message("control_change", channel=channel, control=0, value=bank)))
        self.events.append((0, 0, mido.Message("program_change", channel=channel, program=program)))
        self.events.append((0, 0, mido.Message("control_change", channel=channel, control=7, value=volume)))
        self.events.append((0, 0, mido.Message("control_change", channel=channel, control=10, value=pan)))
        self.events.append((0, 0, mido.Message("control_change", channel=channel, control=91, value=50)))

    def note(self, tick, length, pitch, vel):
        tick, length = max(0, int(tick)), max(10, int(length))
        vel = int(np.clip(vel, 1, 127))
        self.events.append((tick, 2, mido.Message("note_on", channel=self.ch, note=pitch, velocity=vel)))
        self.events.append((tick + length, 1, mido.Message("note_off", channel=self.ch, note=pitch, velocity=0)))

    def midi_track(self):
        track = mido.MidiTrack()
        last = 0
        for tick, _, msg in sorted(self.events, key=lambda e: (e[0], e[1])):
            track.append(msg.copy(time=tick - last))
            last = tick
        return track


def swung(beat):
    """A beat position (in beats, eighths allowed) to ticks, with the off-beat swung."""
    whole = int(beat)
    frac = beat - whole
    if abs(frac - 0.5) < 1e-6:
        return whole * TPB + SWING
    return int(round(beat * TPB))


def chords_in_bar(bar):
    """[(start_tick_in_bar, length, root_pc, quality)]."""
    cs = CHART[bar]
    if len(cs) == 1:
        return [(0, BAR, NOTE[cs[0][0]], cs[0][1])]
    return [(0, BAR // 2, NOTE[cs[0][0]], cs[0][1]), (BAR // 2, BAR // 2, NOTE[cs[1][0]], cs[1][1])]


def voicing(root, quality, low=53, high=69):
    """Rootless four-note voicing (3, 5/13, 7, 9) packed between low and high."""
    third, fifth, seventh, ninth = QUALITY[quality]
    pcs = [(root + third) % 12, (root + seventh) % 12, (root + ninth) % 12,
           (root + (9 if quality != "m7" else fifth)) % 12]
    notes = []
    for pc in pcs:
        p = low + ((pc - low) % 12)
        while p > high:
            p -= 12
        notes.append(p)
    return sorted(set(notes))


def bass_note(pc, lo=33):
    return lo + ((pc - lo) % 12)  # A1 .. G#2


def humanize(rng, tick, amount, on_pulse):
    return tick if on_pulse else tick + rng.randint(-amount, amount)


def build(kind, reps):
    rng = random.Random(1626 if kind == "table" else 20)
    mid = mido.MidiFile(ticks_per_beat=TPB)
    tempo = mido.MidiTrack()
    tempo.append(mido.MetaMessage("set_tempo", tempo=mido.bpm2tempo(BPM), time=0))
    mid.tracks.append(tempo)

    table = kind == "table"
    piano = Track(0, GM["piano"] if table else GM["rhodes"], volume=88 if table else 92, pan=52)
    bass = Track(1, GM["bass"], volume=110, pan=64)
    vibes = Track(2, GM["vibes"], volume=86 if table else 74, pan=80)
    pad = Track(3, GM["pad"], volume=58, pan=64)
    drums = Track(9, BRUSH_KIT, volume=96, pan=64) if table else None

    for rep in range(reps):
        for bar in range(BARS):
            base = (rep * BARS + bar) * BAR
            pulse_bar = bar % 2 == 0          # the board's ring leaves the emblem on this downbeat
            chords = chords_in_bar(bar)
            nxt = chords_in_bar((bar + 1) % BARS)[0]

            # --- Bass
            for i, (start, length, root, quality) in enumerate(chords):
                t0 = base + start
                if table:
                    # Walking quarters: root, chord tone, chord tone/step, approach to the next root.
                    beats = length // TPB
                    third, fifth, seventh, _ = QUALITY[quality]
                    nxt_root = chords[i + 1][2] if i + 1 < len(chords) else nxt[2]
                    r = bass_note(root)
                    line = [r, r + third if beats == 4 else r + fifth, r + fifth, r + seventh - 12 + 12]
                    if beats == 2:
                        line = [r, None]
                    for b in range(beats):
                        if b == beats - 1:
                            target = bass_note(nxt_root)
                            if abs(target - line[0]) > 6:
                                target += -12 if target > line[0] else 12
                            pitch = target + rng.choice((-1, 1))   # chromatic approach
                        else:
                            pitch = line[b]
                        while pitch < 28:
                            pitch += 12
                        while pitch > 52:
                            pitch -= 12
                        on_pulse = pulse_bar and start == 0 and b == 0
                        vel = (104 if on_pulse else 84 + rng.randint(-6, 6)) - (6 if b % 2 else 0)
                        bass.note(humanize(rng, t0 + b * TPB, 8, on_pulse), TPB * 0.92, pitch, vel)
                else:
                    # Two-feel: root, then the fifth, half notes.
                    r = bass_note(root)
                    on_pulse = pulse_bar and start == 0
                    bass.note(t0, min(length, 2 * TPB) * 0.95, r, 100 if on_pulse else 82)
                    if length == BAR:
                        bass.note(t0 + 2 * TPB, 2 * TPB * 0.95, r + 7 if r + 7 <= 50 else r - 5, 74)

            # --- Piano / Rhodes
            for start, length, root, quality in chords:
                t0 = base + start
                v = voicing(root, quality)
                on_pulse = pulse_bar and start == 0
                if table:
                    # Charleston-ish comping: beat 1 short, the "and" of 2 held; plus a pulse accent.
                    hits = [(0, 0.6, 70), (1.5, 1.2, 62)] if length == BAR else [(0, 0.6, 66), (1.5, 0.45, 58)]
                    if rng.random() < 0.35 and length == BAR:
                        hits = [(0.5, 0.4, 60), (2, 0.8, 64), (3.5, 0.4, 58)]
                    if on_pulse and hits[0][0] != 0:
                        hits.insert(0, (0, 0.5, 74))
                    for beat, dur, vel in hits:
                        t = t0 + swung(beat)
                        for k, p in enumerate(v):
                            piano.note(humanize(rng, t + k * 3, 6, on_pulse and beat == 0), dur * TPB,
                                       p, vel + rng.randint(-5, 5) + (8 if on_pulse and beat == 0 else 0))
                else:
                    # Held chord, rolled gently upward; restated softly half way through a full bar.
                    for k, p in enumerate(v):
                        piano.note(t0 + k * 22, length * 0.98, p, (76 if on_pulse else 64) - k * 3)
                    if length == BAR:
                        top = v[-1] + 12
                        piano.note(t0 + swung(2.5), TPB * 1.2, top if top <= 84 else top - 12, 48)

            # --- Pad (menu only): the chord, very soft, under everything.
            if not table:
                for start, length, root, quality in chords:
                    third, fifth, seventh, _ = QUALITY[quality]
                    for p in (48 + ((root - 48) % 12), 60 + ((root + third - 60) % 12), 60 + ((root + seventh - 60) % 12)):
                        pad.note(base + start, length, p, 52)

            # --- Vibes: the tune. The menu sits out of the first A and plays from the bridge.
            if table or bar >= 8:
                for beat, dur, name in TUNE[bar]:
                    t = base + swung(beat)
                    on_pulse = pulse_bar and beat == 0
                    vel = 78 + rng.randint(-8, 8) + (6 if on_pulse else 0) - (0 if table else 10)
                    vibes.note(humanize(rng, t, 10, on_pulse), dur * TPB * 0.95, n(name), vel)

            # --- Brushes (table only)
            if table:
                for beat in range(4):
                    t = base + beat * TPB
                    # Ride: ding, ding-a ding, ding-a
                    drums.note(humanize(rng, t, 5, beat == 0 and pulse_bar), 120, RIDE,
                               (70 if beat in (1, 3) else 58) + rng.randint(-6, 6))
                    if beat in (1, 3):
                        drums.note(humanize(rng, t + SWING, 6, False), 100, RIDE, 46 + rng.randint(-6, 6))
                        drums.note(humanize(rng, t, 6, False), 120, HAT_PEDAL, 54)
                        drums.note(humanize(rng, t, 8, False), 200, SNARE, 36 + rng.randint(-5, 5))
                    # The brush sweep: a soft swish under every beat.
                    drums.note(t, TPB, SNARE_SWISH, 34 + rng.randint(-4, 4))
                if pulse_bar:
                    drums.note(base, 200, KICK, 72)          # feathered kick with the ring
                elif rng.random() < 0.25:
                    drums.note(base + swung(3.5), 150, SNARE, 44)  # a little comment into the pulse bar

    for tr in (piano, bass, vibes, pad) + ((drums,) if drums else ()):
        mid.tracks.append(tr.midi_track())
    return mid


def render(kind, out_dir, soundfont):
    reps = 3
    with tempfile.TemporaryDirectory() as tmp:
        midi_path = Path(tmp) / f"{kind}.mid"
        wav_path = Path(tmp) / f"{kind}.wav"
        build(kind, reps).save(midi_path)
        subprocess.run(["fluidsynth", "-ni", "-q", "-g", "0.6", "-r", str(SR), "-F", str(wav_path),
                        "-o", "synth.reverb.room-size=0.55", "-o", "synth.reverb.level=0.55",
                        "-o", "synth.chorus.active=0", soundfont, str(midi_path)], check=True)
        rate, data = wavfile.read(wav_path)
        assert rate == SR
        data = data.astype(np.float64)
        if data.dtype.kind != "f":
            data /= 32768.0
        loop = int(round(LOOP_SECONDS * SR))
        middle = data[loop:2 * loop]
        assert len(middle) == loop, (len(middle), loop)
        # Level: sit under the effects. RMS to about -21 dBFS, peaks kept under -1 dBFS.
        rms = np.sqrt(np.mean(middle ** 2))
        middle *= 10 ** (-21 / 20) / max(rms, 1e-9)
        peak = np.max(np.abs(middle))
        if peak > 10 ** (-1 / 20):
            middle *= 10 ** (-1 / 20) / peak
        out_wav = Path(tmp) / f"{kind}_loop.wav"
        wavfile.write(out_wav, SR, (middle * 32767).astype(np.int16))
        out = Path(out_dir) / f"music_{kind}.ogg"
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(out_wav), "-c:a", "libvorbis",
                        "-q:a", "5", str(out)], check=True)
        midi_out = Path(out_dir) / f"music_{kind}.mid"
        build(kind, 1).save(midi_out)
        return out, middle


if __name__ == "__main__":
    out_dir = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    out_dir.mkdir(parents=True, exist_ok=True)
    sf = sys.argv[2] if len(sys.argv) > 2 else "/usr/share/sounds/sf2/FluidR3_GM.sf2"
    for kind in ("menu", "table"):
        path, audio = render(kind, out_dir, sf)
        print(f"{path}: {len(audio) / SR:.3f} s, rms {20 * np.log10(np.sqrt(np.mean(audio ** 2))):.1f} dBFS, "
              f"peak {20 * np.log10(np.max(np.abs(audio))):.1f} dBFS")
