# Crowd sprites

Faceless solid-black cutouts on stands, PaRappa-style flat shapes. Mood reads from pose alone.

| File | |
|---|---|
| `crowd_beanie.png`, `crowd_afro.png`, `crowd_bucket.png`, `crowd_spiky.png` | one sprite sheet per crowd member type |
| `crowd_*.svg` | editable vector source of each sheet |
| `sheet.json` | grid layout, mood names, pivot |
| `preview.png` | all four types, frame A |
| `make_crowd_sprites.py` | regenerates everything (`python art/crowd/make_crowd_sprites.py`, needs Inkscape); `--color` gives flat-colour cardboard cutouts with faces instead |

**Club style** (`club/`, what the venue uses): `python art/crowd/make_crowd_sprites.py --club --human --out art/crowd/club`.
Same layout: people-proportioned figures (`--human`: a head about a sixth of their height) for the dark club, almost
black with only a hint of their colours, dimmed faces, a thin dull edge so people standing in front of each other
don't merge, and no stand (the feet are on the floor at the pivot; a standing head tops out about 110 px down the
cell, the Crowd's Head Top Px). Movement and shape read; nothing pulls focus from the stage. The black silhouettes
above stay the source for the mid and far tiers.

**Layout:** each sheet is 12 x 2 cells of 256 x 384 px, transparent background.
Column = crowd mood, matching the game's crowd level 0-11 (`round(state.crowd.level)`):

0 dead (slumped on a phone) · 1 silent · 2 restless (arms crossed) · 3 polite (clapping) · 4 warming up ·
5 nodding · 6 into it (one arm up) · 7 cheering (both arms up) · 8 hyped (jumping) · 9 going wild ·
10 losing it · 11 raise the roof

Row = frame A / B: flip between them on the beat (PaRappa-style two-frame "boil").
Pivot (anchor) is the foot of the stand: (128, 336) in each cell.

**Making the crowd feel alive** (what `unity/Assets/Scripts/Venue/Crowd.cs` does with the club sheets):

- every cutout flips on the beat or the off-beat, so they don't move in lockstep
- the four types are mixed, some flipped horizontally, with varied scale and position
- each cutout switches to a new mood column after its own small delay, so the room ripples instead of snapping
- everyone bobs on the beat, deeper the livelier the room, and from "into it" up they hop

## Stadium tiers

Not used by the club venue, which only has the near tier (the `club/` sheets). For a ~50k crowd on Quest, only the people near the stage are individual sprites (`make_crowd_lods.py`
builds the rest from the same drawings; decorations are left out because they shimmer at small sizes):

| Tier | Asset | Use |
|---|---|---|
| near (~100-300) | `crowd_*.png` (256x384 cells) | GPU-instanced quads, random type / mirror / scale / beat offset |
| mid (~2-5k) | `mid/crowd_mid_atlas.png` (768x768, 64x96 cells; `mid/atlas.json`) | one instanced material; per-instance seed picks the type row, `round(level)` the column, beat parity the frame |
| far (the rest) | `far/strip_{calm,into_it,wild}_{A,B}.png` (2048x192, tile horizontally; `far/strips.json`) | tile along the stands; set by crowd level (calm 0-3, into_it 4-7, wild 8-11), swap A/B on the beat, bounce in the shader: amplitude from crowd level, phase per 32 px slot (`hash(slot + section)`) |

Half the figures in each far strip sit on the other frame, so an A/B swap never looks like lockstep.
`far/stadium_mock.png` shows stacked strips. Silhouettes need light behind them (bright stands, backdrop
or haze); against dark stands they disappear. Use alpha-clip materials (not blending) with
alpha-to-coverage + MSAA to keep overdraw and edge shimmer down.
