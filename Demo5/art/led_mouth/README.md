# LED mouth

The robot's mouth as a 16 x 8 LED panel: one frame per viseme (mouth shape for speech), plus five expressions.
One colour, three brightnesses: lips full, teeth about half, tongue about a third.

| File | |
|---|---|
| `led_mouth_mask.png` | every frame at one pixel per LED (80 x 32, greyscale = brightness): for a panel material |
| `led_mouth.png` | every frame drawn as LED dots (5 x 4 cells of 256 x 128 px), white on transparent, unlit dots faint: for sprites |
| `sheet.json` | layout, frame names, each frame's pixels as text rows, the Rhubarb / uLipSync mappings |
| `preview.png` | every frame, lit, labelled |
| `make_led_mouth.py` | regenerates everything (`python art/led_mouth/make_led_mouth.py [--color "#38e8ff"]`); the frames are text art at the top |

**Layout:** frame `i` is at column `i % 5`, row `i // 5` (row 0 at the top), in both sheets.

Frames 0-14 are Meta's 15 visemes in Meta's order, the same as the cartoon mouth (`art/mouth`): 0 sil, 1 PP, 2 FF,
3 TH, 4 DD, 5 kk, 6 CH, 7 SS, 8 nn, 9 RR, 10 aa, 11 E, 12 ih, 13 oh, 14 ou. So the cartoon mouth's drivers work
unchanged (Meta face tracking: the strongest viseme; Rhubarb and uLipSync: `sheet.json` `rhubarb` / `ulipsync`).

Frames 15-19 are expressions for when the robot isn't talking: 15 smile, 16 grin, 17 frown, 18 smirk, 19 grr
(gritted teeth). They fit the opponent's moods (pleased, cocky, hurt, unimpressed, annoyed).

## In Unity

- **Panel material (crispest):** import `led_mouth_mask.png` with Filter Mode *Point*, mipmaps off, compression
  *None*. Use it as the emission map of the face plate's mouth area, tinted with the emission colour, and pick the
  frame with the texture offset and scale (scale `(1/5, 1/4)`, offset `(col/5, 1 - (row+1)/4)`). Multiply by a
  tiling round-dot texture (16 x 8 repeats) for the LED look.
- **Sprites (simplest):** slice `led_mouth.png` into a 5 x 4 grid and swap sprites. It's white, so tint it with the
  sprite colour (or an emissive material) to match the robot.

Hold each frame at least ~60 ms so fast rap doesn't flicker. On a real LED panel a frame snaps rather than
morphs, so no blending between frames is needed.

# LED eyes

Two 8 x 8 panels to go with the mouth (16 x 8 per frame, same three brightnesses), from `make_led_eyes.py`
(`python art/led_mouth/make_led_eyes.py [--color ...]`). `led_eyes_mask.png` / `led_eyes.png` are laid out like the
mouth's; in each frame the viewer's left eye (the robot's right) is the left half, the other eye the right half.

| # | Frame | For |
|---|---|---|
| 0 | neutral | calm, idle, listening |
| 1, 2 | half_blink, blink | blinking: `blink` in `eyes.json` is half, closed, half (~40 ms each, every few seconds) |
| 3-6 | look_left, look_right, look_up, look_down | glances; up while writing the comeback |
| 7 | happy | amused, laughs |
| 8 | cocky | cocky: half-lidded, looking askance |
| 9 | heated | heated: brows down toward the middle |
| 10 | glare | glares |
| 11 | rattled | rattled: brows up in the middle, looking down |
| 12 | wince | winces (> <) |
| 13 | surprised | steps back |
| 14 | impressed | respectful: wide, pupils big |
| 15 | skeptical | shrugs: one eye narrowed |
| 16 | wink | after a punchline |
| 17 | focused | rapping, nods |
| 18 | defeated | lost the battle (X X) |
| 19 | processing | loading, boot |

`eyes.json` `moods` and `gestures` pair the game's opponent moods (`state.opponent.mood`: calm, cocky, rattled,
heated, respectful, amused) and gestures (laughs, shrugs, nods, glares, winces, steps_back) with an eyes frame and a
mouth frame (the mouth's expression shows while it isn't talking); `face_preview.png` shows them all as whole faces.
