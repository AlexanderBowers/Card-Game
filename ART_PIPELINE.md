# Aim for 20 — Art pipeline (Blender + Krita → Godot)

Goal: cards and tables with the finish of **Pokémon TCG Pocket** — clean, bright, tactile, and
obviously "premium" when you pick a card up. Art is made in **Blender** (3D-rendered pieces) and
**Krita** (painting, textures, paint-over); Godot adds the live layer (foil, glow, tilt, bloom).

Everything below drops in **by file name**. Save a PNG in the right folder and the game uses it on
the next launch; anything not made yet falls back to what the game draws today.

---

## 1. What makes Pocket feel premium (and what that means for us)

| Pocket does | So we… |
|---|---|
| Every card is a physical object: rounded corners, a visible edge, soft shadow | Render the card **frame as a 3D slab** in Blender (bevelled rounded rectangle, ~1.5 mm thick) so the border has real light on it |
| One consistent light: soft key from the top-left, gentle rim | **One lighting rig** for every render (below) — never relight per card |
| Art is layered: frame, illustration, foil areas | Deliver a **colour PNG** plus, for special cards, a **foil mask** (greyscale) — Godot animates the shine |
| Rarity reads instantly from the frame | A clear **frame hierarchy**: main < Modifier < +/- < effect (foil) < rescue |
| Big, clean typography; nothing tiny | **No text baked into cards.** The game draws numbers, pips and signs on top in the card's ink. Effect cards may carry their name in the art |
| Saturated, clean colour; no muddy darks | Keep values light-to-mid on faces; darks only in the table |
| Tables are calm; cards pop | Playmats are **low-contrast** in the board and hand zones |

## 2. The card

- **Canvas:** 560 × 760 px PNG with alpha (the current size). Work at **2× (1120 × 1520)** and
  export at 1×; keep the 2× masters in a `art_src/` folder outside `assets/` (not shipped).
- **Corner radius:** ~7% of the width (≈ 40 px at 1×). Transparent outside the card.
- **Safe zones — keep these clean, the game draws here:**
  - Top-left corner index: x 0–55%, y 0–30% (big numbers, e.g. "10", "+3")
  - Bottom-right corner index: x 45–100%, y 70–100% (the same, turned round)
  - Centre: x 28–72%, y 20–84% (pips, the drawn + / − sign)
  - Effect cards with their own illustration have **no** game text on them — use the whole face.
- **In the hand the bottom ~45% of a card is off screen in portrait.** Put the identity (colour,
  emblem, rarity) in the top half.

### Frames to make (`assets/aimfor20_art/`)

| File | Card | Notes |
|---|---|---|
| `card_main.png` | Main deck 1–10 | Neutral light frame; the game tints it per rank until rank faces exist |
| `cards/card_main_bronze.png` … `_silver`, `_gold`, `_ruby`, `_obsidian` | Main deck, per rank | Optional; replaces the tint. Shown as painted |
| `card_plus.png` | +n Modifier | Blue family |
| `card_minus.png` | −n Modifier | Red family |
| `card_flip.png` | +/− Modifier | Violet; the game draws blue + over red − in the two halves |
| `card_foil.png` | Effect card fallback | Gold foil frame, used until an effect has its own art |
| `cards/effect_copy.png` | Copy | Full illustrated face (see §3) |
| `cards/effect_tradetotals.png` | Trade Totals | |
| `cards/effect_shave.png` | Shave | |
| `cards/effect_tradehands.png` | Trade Hands | |
| `cards/effect_recall.png` | Recall | |
| `cards/effect_veto.png` | Veto | |
| `cards/rescue.png` | Rescue card | Distinct "emergency" frame (teal/white, a flare or life-ring motif). The game draws the sign and pips on it |
| `card_back.png` | Card back | Default back |
| `backs/card_back_bronze.png` … | Per-rank backs | Optional; the gilded collection back still wins when earned |

### Effect illustrations (§ the ones called out in playtest)

One strong central emblem each, readable at 84 × 114 px (squint test: shrink to that size in Krita
before calling it done). Suggested motifs:

- **Copy** — two cards, one a mirror image of the other; a soft duplicate glow.
- **Trade Totals** — two score tokens crossing over, arrows forming a loop.
- **Trade Hands** — two hands of cards passing each other.
- **Shave** — a blade taking a sliver off a number/coin.
- **Recall** — a card returning along a curved arrow into a hand.
- **Veto** — a card stamped / struck through, a bold seal.

Name at the top in the card's own lettering is fine (these are the only cards with baked text).

## 3. Blender setup (one file, every render)

1. **Scene:** 1120 × 1520, **orthographic** camera looking straight down, card filling frame with
   the 7% corner radius; **Film → Transparent** on.
2. **Card mesh:** plane → rounded corners (Bevel modifier on vertices, 8+ segments) → Solidify
   1.5 mm → Bevel modifier on edges (0.4 mm, 3 segments) so the rim catches light.
3. **Light rig (never change it):** area key light top-left at ~45°, soft (size ≥ 1 m), strength
   tuned so white frames sit at ~92% value; a weak fill from the right; a thin rim from the top.
   Save it as the rig collection and link it into every card file.
4. **Engine:** EEVEE (fast) with soft shadows, or Cycles for final effect cards. Filmic/AgX
   "Medium High Contrast" for punchy but clean colour.
5. **Materials:** frame = slightly glossy paint (roughness 0.35); foil areas = metallic 1.0,
   roughness 0.2 — and ALSO render them to a separate **foil mask** (next).
6. **Passes to export per card:**
   - `name.png` — beauty render, alpha.
   - `name_foil.png` — greyscale mask, white where the card is foil (a holdout/emission pass, or
     paint it in Krita). Only for effect cards, rescue, and per-rank main faces if they have foil.
7. **Emblems** (effect illustrations): model low-poly/clean shapes, render in the same rig, then
   composite onto the frame in Krita. Keep emblems in the centre safe zone.

## 4. Krita finishing

- Paint-over for edge highlights, subtle paper/grain texture at ≤ 5% opacity, colour polish.
- Check at **1× and at 84 × 114** (the board size on a phone) before exporting.
- Export PNG, 8-bit, sRGB, alpha on. No embedded ICC surprises.

## 5. Tables (playmats) — "more interesting tables per stage"

- **Files:** `playmats/playmat_<rank>_portrait.png` (1080 × 1920) and
  `playmats/playmat_<rank>_landscape.png` (1920 × 1080), ranks: bronze, silver, gold, ruby,
  obsidian. Shown as painted (no tint) when present.
- **Theme per rank** (suggested): Bronze — worn green felt in a tavern; Silver — cool slate and
  brushed metal; Gold — lacquered table, gold inlay; Ruby — velvet and candlelight; Obsidian —
  dark glass with faint violet veins.
- **Keep calm where the game plays:** the two boards (3 × 3 grids) sit left-of-centre in each half,
  the decks to their right, the centre line and ring across the middle, the hands at the top and
  bottom edges. Detail and contrast go to the **edges and corners**; the play zones stay within
  ±10% brightness of their average so cards always pop.
- Paint in Krita over a Blender-rendered base (table surface + inlay + lighting falloff).

## 6. Decks

- Deck backs per rank (above) make the deck on the table change with the stage. Same canvas and
  corner radius as the faces, strong central emblem, symmetrical (it is seen both ways up).

## 7. What Godot adds (code, not art)

These are the live layer that gives the Pocket "shine" and are built in code once the art starts
arriving:
- **Holo foil shader** driven by the `_foil.png` mask: a moving rainbow/gold sheen.
- **Tilt when held:** the picked-up card leans toward your finger, with the foil and a gloss
  highlight sliding across it.
- **Bloom / glow** on foil and on the on-target score box.
- Impact on play (done): lift, hover, slam, ring, board jolt.

## 8. Checklist per asset

- [ ] Right size, alpha, corner radius, sRGB PNG
- [ ] Safe zones clear (unless it is an illustrated effect card)
- [ ] Reads at 84 × 114
- [ ] Top half carries the identity
- [ ] Same light rig as every other card
- [ ] Foil mask exported (special cards)
- [ ] Saved under the exact file name in §2 / §5
