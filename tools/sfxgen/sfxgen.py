"""Critical Count's placeholder sound effects, synthesised from scratch (2026-10-22).

Run:  python tools/sfxgen/sfxgen.py            (writes every sound into assets/sfx/)
      python tools/sfxgen/sfxgen.py set_win    (just the ones named)

Why a script: these are stand-ins until Alexander makes his own, and a script is something you
can tweak and re-run - change a note, a decay, a gain, run it, listen in the game. Every sound the
game plays lives in assets/sfx/ under the name below, so a hand-made replacement is a drop-in:
save your .wav over the file of the same name and the game picks it up. Nothing here is needed
at run time.

The brief, from an audio engineer's playtest (Ian, 2026-10-22):
  * a tiny tap the instant a button is pressed - "straddling the boundary between haptic feedback
    and sound feedback"
  * the draw split in two: a swish as the card leaves the deck, a slap exactly when it lands
  * Hold: the press tap, then ear candy as the lock shuts - "a little bit of metal, like shing"
  * picking up and playing a Modifier: subtly POSITIVE, "you just touched something special" -
    the old ones fell in pitch and read as failure
  * a celebration for winning a set, a bigger one for the match
  * counters that go up (medals) tick as they count and finish on a chime

Every sound starts on its first sample (no silence in front), so the code can fire it on the
exact frame of the thing it belongs to.
"""

import sys
from pathlib import Path

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, lfilter, sosfilt

SR = 44100
OUT = Path(__file__).resolve().parents[2] / "assets" / "sfx"
RNG = np.random.default_rng(20261022)


# ----------------------------------------------------------------------------------------------
# Building blocks
# ----------------------------------------------------------------------------------------------

def t_axis(seconds):
    return np.arange(int(SR * seconds)) / SR


def env(seconds, attack=0.002, decay=0.2):
    """Instant-ish attack, exponential decay. `decay` is the time to fall to ~1/e."""
    t = t_axis(seconds)
    return np.clip(t / max(attack, 1e-5), 0, 1) * np.exp(-t / decay)


def note(name):
    """Frequency of a note name like 'C6' or 'F#5'."""
    steps = {"C": -9, "D": -7, "E": -5, "F": -4, "G": -2, "A": 0, "B": 2}
    letter, rest = name[0], name[1:]
    shift = 0
    if rest.startswith("#"):
        shift, rest = 1, rest[1:]
    elif rest.startswith("b"):
        shift, rest = -1, rest[1:]
    semis = steps[letter] + shift + (int(rest) - 4) * 12
    return 440.0 * 2 ** (semis / 12)


def bell(freq, seconds=0.8, decay=0.25, bright=1.0):
    """A small struck bell / glockenspiel: slightly inharmonic partials, the high ones dying first."""
    t = t_axis(seconds)
    partials = [(1.0, 1.0, 1.0), (2.0, 0.45 * bright, 0.6), (2.76, 0.28 * bright, 0.4),
                (5.40, 0.12 * bright, 0.22), (8.93, 0.05 * bright, 0.12)]
    out = np.zeros_like(t)
    for ratio, amp, life in partials:
        if freq * ratio > SR / 2.2:
            continue
        out += amp * np.sin(2 * np.pi * freq * ratio * t) * np.exp(-t / (decay * life))
    return faded(out * np.clip(t / 0.0015, 0, 1))


def marimba(freq, seconds=0.5, decay=0.12):
    """Wooden and round: a fundamental and its 4th and 10th harmonics, which die fast."""
    t = t_axis(seconds)
    out = np.sin(2 * np.pi * freq * t) * np.exp(-t / decay)
    out += 0.35 * np.sin(2 * np.pi * freq * 4 * t) * np.exp(-t / (decay * 0.25))
    if freq * 10 < SR / 2.2:
        out += 0.15 * np.sin(2 * np.pi * freq * 10 * t) * np.exp(-t / (decay * 0.08))
    return faded(out * np.clip(t / 0.001, 0, 1))


def noise(seconds):
    return RNG.uniform(-1, 1, int(SR * seconds))


def band(x, lo, hi, order=2):
    return sosfilt(butter(order, [lo, min(hi, SR / 2.1)], btype="band", fs=SR, output="sos"), x)


def low(x, cutoff, order=2):
    return sosfilt(butter(order, cutoff, btype="low", fs=SR, output="sos"), x)


def high(x, cutoff, order=2):
    return sosfilt(butter(order, cutoff, btype="high", fs=SR, output="sos"), x)


def faded(sig, seconds=0.03):
    """The last few milliseconds faded out, so a layer cut short never ends on a click."""
    sig = np.copy(sig)
    n = min(len(sig), int(SR * seconds))
    sig[-n:] *= np.linspace(1, 0, n) ** 2
    return sig


def place(canvas_seconds, *layers):
    """Mixes (start_seconds, signal) layers onto one canvas."""
    out = np.zeros(int(SR * canvas_seconds))
    for start, sig in layers:
        sig = faded(sig)
        i = int(SR * start)
        n = min(len(sig), len(out) - i)
        if n > 0:
            out[i:i + n] += sig[:n]
    return out


def room(x, mix=0.14, size=1.0):
    """A small, soft room (Schroeder: four combs into two allpasses), so a sound sits in a space
    instead of in your ear canal. Adds a tail; the dry onset is untouched."""
    x = np.concatenate([x, np.zeros(int(SR * 0.6 * size))])
    wet = np.zeros_like(x)
    for delay_ms, fb in [(29.7, 0.77), (37.1, 0.75), (41.1, 0.73), (43.7, 0.71)]:
        d = int(SR * delay_ms / 1000 * size)
        a = np.zeros(d + 1); a[0] = 1; a[d] = -fb
        wet += lfilter([1.0], a, x)
    for delay_ms, g in [(5.0, 0.7), (1.7, 0.7)]:
        d = int(SR * delay_ms / 1000)
        b = np.zeros(d + 1); b[0] = -g; b[d] = 1
        a = np.zeros(d + 1); a[0] = 1; a[d] = -g
        wet = lfilter(b, a, wet)
    wet = low(wet, 6000)
    wet /= max(1e-9, np.max(np.abs(wet)))
    return (1 - mix) * x / max(1e-9, np.max(np.abs(x))) + mix * wet


def trim(x, floor_db=-60):
    """Cuts the silent end off (never the front) and fades the last few milliseconds."""
    level = 10 ** (floor_db / 20) * np.max(np.abs(x))
    loud = np.nonzero(np.abs(x) > level)[0]
    end = min(len(x), loud[-1] + int(SR * 0.01)) if len(loud) else len(x)
    x = np.copy(x[:end])
    n = min(len(x), int(SR * 0.02))
    x[-n:] *= np.linspace(1, 0, n)
    return x


def write(name, x, peak_db=-3.0):
    x = trim(x)
    x = x / max(1e-9, np.max(np.abs(x))) * 10 ** (peak_db / 20)
    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / f"{name}.wav"
    wavfile.write(path, SR, (x * 32767).astype(np.int16))
    print(f"  {path.name:22s} {len(x) / SR:5.2f}s  peak {peak_db:+.0f} dB")


# ----------------------------------------------------------------------------------------------
# The sounds
# ----------------------------------------------------------------------------------------------

def ui_tap():
    # Every button, on the press itself. A soft, short wooden tick - so quiet and quick it reads
    # as the button having a physical edge, not as a sound effect.
    t = t_axis(0.05)
    body = np.sin(2 * np.pi * 1850 * t) * np.exp(-t / 0.009)
    click = high(noise(0.05), 3000) * np.exp(-t / 0.0025) * 0.5
    write("ui_tap", body + click, peak_db=-10)


def card_swish():
    # The card leaving the deck: air past a card, swelling and gone before it lands (the flight
    # is 0.35s; the slap takes over there).
    seconds = 0.26
    t = t_axis(seconds)
    n = noise(seconds)
    out = np.zeros_like(t)
    chunks = 26
    size = len(t) // chunks
    for k in range(chunks):  # a band sweeping upward, as the card speeds away from the deck
        lo = 900 + 2600 * k / chunks
        seg = band(n, lo, lo * 2.2)[k * size:(k + 1) * size]
        out[k * size:k * size + len(seg)] = seg
    shape = np.sin(np.pi * np.clip(t / seconds, 0, 1)) ** 1.6
    write("card_swish", low(out * shape, 9000), peak_db=-9)


def card_slap():
    # The card hitting the felt: all transient, on the very first sample, with a little thump of
    # table under it. No pre-roll - this is what has to line up with the landing frame.
    t = t_axis(0.16)
    crack = low(noise(0.16), 4500) * np.exp(-t / 0.006)
    thump = np.sin(2 * np.pi * 150 * t) * np.exp(-t / 0.035) * 0.8
    paper = band(noise(0.16), 2000, 7000) * np.exp(-t / 0.02) * 0.25
    write("card_slap", room(crack + thump + paper, mix=0.08, size=0.5), peak_db=-5)


def modifier_pick():
    # Lifting a Modifier out of the hand: a soft glint going UP - "you touched something special",
    # not "you won". Two light bell notes a fifth apart, the second barely there.
    x = place(0.6, (0.0, bell(note("E6"), 0.5, 0.11, bright=0.6) * 0.7),
                   (0.045, bell(note("B6"), 0.5, 0.13, bright=0.5) * 0.45))
    write("modifier_pick", room(x, mix=0.16, size=0.7), peak_db=-11)


def modifier_flip():
    # The +/- card flipping sign: a quick flick and a tiny rising chirp.
    t = t_axis(0.12)
    chirp = np.sin(2 * np.pi * (1400 * t + 4200 * t ** 2)) * np.exp(-t / 0.03)
    flick = high(noise(0.12), 2500) * np.exp(-t / 0.004) * 0.6
    write("modifier_flip", chirp + flick, peak_db=-12)


def modifier_impact():
    # A Modifier slammed onto the board. The old one fell in pitch and read as failure (it worked
    # as a "they've got you" sound only by accident). Now: a solid body - steady, not dropping -
    # and a quick rising sparkle on top, so playing one feels like using something special.
    t = t_axis(0.7)
    body = np.sin(2 * np.pi * 110 * t) * np.exp(-t / 0.07)
    body += 0.5 * np.sin(2 * np.pi * 220 * t) * np.exp(-t / 0.05)
    crack = low(noise(0.7), 3500) * np.exp(-t / 0.008) * 0.9
    sparkle = place(0.7, (0.02, bell(note("G6"), 0.6, 0.12, 0.7) * 0.32),
                         (0.065, bell(note("C7"), 0.6, 0.14, 0.7) * 0.28),
                         (0.11, bell(note("E7"), 0.6, 0.18, 0.6) * 0.24))
    write("modifier_impact", room(body + crack + sparkle, mix=0.15, size=0.8), peak_db=-4)


def hold_lock():
    # The padlock shutting on Hold (TableMoments.PlayHold fires it the frame the shackle closes):
    # two quick mechanical clicks, then a bright metallic "shing" that rings out. The partials are
    # inharmonic, like a struck steel bar, and glide up a hair so it shines rather than clanks.
    t = t_axis(0.9)
    click1 = high(noise(0.9), 1500) * np.exp(-t / 0.003)
    click2 = np.concatenate([np.zeros(int(SR * 0.018)), click1])[:len(t)] * 0.7
    shing = np.zeros_like(t)
    glide = 1 + 0.012 * (1 - np.exp(-t / 0.05))
    for f, amp, life in [(2120, 1.0, 0.32), (3390, 0.7, 0.24), (5270, 0.45, 0.17), (7930, 0.25, 0.1),
                         (11400, 0.12, 0.06)]:
        shing += amp * np.sin(2 * np.pi * f * glide * t) * np.exp(-t / life)
    shing *= np.clip((t - 0.012) / 0.004, 0, 1) * 0.42
    ring = band(noise(0.9), 6000, 12000) * np.exp(-t / 0.05) * 0.15  # the "shhh" of the shing
    write("hold_lock", room(faded(click1 + click2 + shing + ring, 0.2), mix=0.18, size=0.9), peak_db=-5)


def set_win():
    # A set won: a quick bright arpeggio up a major chord, ending on a held glint.
    notes = ["C6", "E6", "G6", "C7"]
    layers = [(i * 0.065, marimba(note(n), 0.6, 0.16) * (0.8 + 0.07 * i)) for i, n in enumerate(notes)]
    layers.append((0.2, bell(note("C7"), 1.0, 0.3, bright=0.8) * 0.35))
    write("set_win", room(place(1.2, *layers), mix=0.18), peak_db=-4)


def set_lose():
    # A set lost: soft and short - a little "aw", two muted notes stepping down. Never harsh: it
    # is half the sets in a match.
    layers = [(0.0, marimba(note("G4"), 0.5, 0.12) * 0.8), (0.12, marimba(note("D4"), 0.6, 0.16) * 0.7)]
    write("set_lose", room(low(place(0.9, *layers), 2500), mix=0.15), peak_db=-9)


def match_win():
    # The match: a run up two octaves, then the whole chord rung together with a shimmer over it.
    run = ["C5", "E5", "G5", "C6", "E6", "G6"]
    layers = [(i * 0.07, marimba(note(n), 0.7, 0.18) * 0.8) for i, n in enumerate(run)]
    chord_at = len(run) * 0.07 + 0.05
    for n, a in [("C6", 0.5), ("E6", 0.42), ("G6", 0.42), ("C7", 0.5), ("E7", 0.3)]:
        layers.append((chord_at, bell(note(n), 2.2, 0.75, bright=0.7) * a))
        layers.append((chord_at, marimba(note(n) / 2, 1.0, 0.35) * a * 0.6))
    t = t_axis(1.2)
    shimmer = band(noise(1.2), 7000, 14000) * np.exp(-t / 0.28) * np.clip(t / 0.15, 0, 1) * 0.06
    layers.append((chord_at, shimmer))
    for i, n in enumerate(["C8", "G7", "E8", "C8", "G7", "E7", "C8"]):  # sparkles over the tail
        layers.append((chord_at + 0.12 + i * 0.11, bell(note(n), 0.4, 0.07, 0.4) * 0.12))
    write("match_win", room(place(3.0, *layers), mix=0.2, size=1.2), peak_db=-2)


def match_lose():
    # The match lost: three notes walking down a minor chord, unhurried. Disappointing, not a
    # buzzer - the next match is one tap away.
    layers = [(0.0, marimba(note("E5"), 0.7, 0.2) * 0.7), (0.18, marimba(note("C5"), 0.7, 0.2) * 0.7),
              (0.36, marimba(note("A4"), 1.2, 0.38) * 0.8), (0.36, bell(note("A5"), 1.2, 0.35, 0.4) * 0.15)]
    write("match_lose", room(low(place(1.8, *layers), 3000), mix=0.2), peak_db=-6)


def medal_tick():
    # One medal counting up: a tiny coin tick. The game raises its pitch a little with every medal
    # (AudioStreamPlayer.PitchScale), so a big win climbs.
    x = bell(note("E7"), 0.18, 0.035, bright=0.5) + high(noise(0.18), 5000) * env(0.18, 0.0005, 0.003) * 0.3
    write("medal_tick", x, peak_db=-12)


def medal_done():
    # The count has landed: two coins and a glint.
    layers = [(0.0, bell(note("G6"), 0.9, 0.22, 0.9) * 0.7), (0.07, bell(note("D7"), 0.9, 0.28, 0.9) * 0.6),
              (0.14, bell(note("G7"), 1.0, 0.35, 0.7) * 0.4)]
    write("medal_done", room(place(1.3, *layers), mix=0.18), peak_db=-6)


SOUNDS = {f.__name__: f for f in [ui_tap, card_swish, card_slap, modifier_pick, modifier_flip,
                                   modifier_impact, hold_lock, set_win, set_lose, match_win,
                                   match_lose, medal_tick, medal_done]}

if __name__ == "__main__":
    wanted = sys.argv[1:] or list(SOUNDS)
    print(f"Writing to {OUT}")
    for name in wanted:
        SOUNDS[name]()
