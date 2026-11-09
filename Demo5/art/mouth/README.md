# Mouth sprites

A flat 2D cartoon mouth, one frame per viseme (mouth shape for speech), for lip sync by swapping frames.

| File | |
|---|---|
| `mouth.png` | the sprite sheet, transparent background |
| `mouth.svg` | editable vector source |
| `sheet.json` | grid layout, frame names, and the Rhubarb / uLipSync mappings |
| `preview.png` | every frame, labelled |
| `make_mouth_sprites.py` | regenerates everything (`python art/mouth/make_mouth_sprites.py`, needs Inkscape) |

**Layout:** 5 x 3 cells of 256 x 256 px. Frame `i` is at column `i % 5`, row `i // 5` (row 0 at the top).
Pivot (the mouth's centre) is (128, 128) in each cell.

The frames are Meta's 15 visemes, in Meta's order, so frame `i` is viseme `i` (the order of `FaceSource.visemes`
in the demo repo, and of Meta face tracking):

| # | Name | Sounds | Example |
|---|---|---|---|
| 0 | sil | silence | (between words) |
| 1 | PP | P, B, M | Pop, Bob, Mom |
| 2 | FF | F, V | Fun, Very |
| 3 | TH | TH | THink, THis |
| 4 | DD | T, D | Top, Dog |
| 5 | kk | K, G | Kick, Go |
| 6 | CH | CH, J, SH | CHurch, Jump, SHe |
| 7 | SS | S, Z | Sun, Zoo |
| 8 | nn | N, L | No, Love |
| 9 | RR | R | Red, caR |
| 10 | aa | AH | fAther, hOt, cUp |
| 11 | E | EH, AE | bEd, cAt |
| 12 | ih | IH, EE | bIt, sEE |
| 13 | oh | OH, AW | gO, sAW |
| 14 | ou | OO, W | fOOd, Win |

## Driving it

- **Meta face tracking** (the player): show the frame of the strongest of the 15 viseme weights.
- **Rhubarb Lip Sync** (the opponent, cues from the server): `sheet.json` `rhubarb` maps its cues to frames:
  A→PP, B→SS, C→E, D→aa, E→oh, F→ou, G→FF, H→nn, X→sil.
- **uLipSync**: `sheet.json` `ulipsync` maps its phonemes: A→aa, I→ih, U→ou, E→E, O→oh, N→nn, silence (`-`)→sil.

Hold each frame at least ~60 ms so fast rap doesn't flicker, and fall back to `sil` when the voice is quiet.
Scaling the sprite up a little with loudness (~5-10%) adds punch.

Colours, line weights and each frame's proportions are constants and `Shape` entries at the top of the script.
