# Freestyle Simulator backend

AI services for the rap-battle game. The game itself (rounds, turns, crowd, opponent mood, score) runs in the
client: the Quest app ([`../unity`](../unity/README.md)), the browser GUI (`web/`) or `tools/test_client.py`. The
client streams mic audio over a WebSocket and says when the user's bars start and end; the server transcribes it
live, measures flow, asks Jev to rate each stretch of bars, and has an LLM write (and a voice rap) the opponent's
comeback. Ratings stream back, and the client's game rules turn them into the crowd, the mood and the score.
Protocol: [PROTOCOL.md](PROTOCOL.md).

```
Quest / test client ──PCM16 16 kHz──▶ /ws/battle
                                         │
              ┌──────────────────────────┼─────────────────────────┐
              ▼                          ▼                         ▼
   transcribe.py (Whisper /    audio.py + metrics.py       lyrics.py (rhyme
   Deepgram / OpenRouter)      10 ms frames: syllables,    density, multis,
              │                pauses, pocket vs beat      end rhymes)
              └──────────────┬───────────┴─────────────────────────┘
                             ▼  chunker: every 6 words, or a pause after 2+
              rater.py ── two Jev calls in parallel per chunk
                 ├─ crowd:    reaction 0-11, punchline, fell off, flow, lyricism
                 └─ opponent: intimidated, entertained, confident, annoyed, impressed, gesture
                             ▼
                             ▼  `rating` to the client
   client rules (game.py, ported) ── crowd level (instant pop + rising base), opponent mood, points
                             ▼  the client asks for the comeback (`verse`: mood, score, the lines that hurt)
              opponent.py ── OpenRouter LLM writes it; a voice raps it; rated 2 lines at a time
```

## Run it

From the project root (`Demo5/`):

```bash
./setup.sh                    # .venv + dependencies (GPU extras when an NVIDIA GPU is present)
$EDITOR backend/.env          # put your OPENROUTER_API_KEY in it
./run.sh                      # uvicorn on 0.0.0.0:8000
```

The venv's `activate` script puts the CUDA 12 cuBLAS/cuDNN wheels on `LD_LIBRARY_PATH`, which
faster-whisper needs for the GPU, so always `source .venv/bin/activate` (run.sh does) rather than
calling `.venv/bin/python` directly.

Open http://localhost:8000, wear headphones, set the BPM / bars / rounds, press **Start battle**.
The metronome runs at that BPM for the whole battle. You get a 2-bar count-in, rap, and press
**End my turn** (or the turn ends after the bar count). While the LLM writes, the opponent
ad-libs on the beat ("my turn", "uh huh"); then it comes in on the next bar and raps its verse,
one line per bar. The default voice is "MC voice (Qwen)" (or whichever server engine runs): the LLM gives each line a delivery
direction ("aggressive, leaning in"; requests for laughs or sighs are filtered out, since the model
performs them literally), Qwen3-TTS raps each line with it, and WSOLA stretches each line to fill its bar.
Lines too long for that are re-rendered with a fast delivery, then cut with a fade at the bar line,
so nothing runs into the next bar. The LLM gets a per-bar syllable budget from the BPM (4.8 syllables/s). Qwen loads in the background at startup (~10 s once downloaded); battles
started before that use the page's voice (the browser's voice, or espeak via "Robot voice (espeak)").

`http://localhost:8000/?clip=clips/demo-verse.wav` plays a spoken test verse in place of the mic,
for trying the loop without rapping.

Without a key everything still runs: heuristic rater, template opponent.

```bash
# CLI client instead of the browser
python tools/test_client.py --mic --bpm 90 --bars 16 -v

# Fully offline smoke test: synthetic clip, scripted transcript
python tools/make_test_wav.py out/tight.wav --bpm 90
TRANSCRIBER=scripted SCRIPTED_TRANSCRIPT=out/tight.words.json uvicorn freestyle.server:app --port 8000
python tools/test_client.py --wav out/tight.wav --bpm 90 --bars 8

python -m pytest -q   # 39 tests

# Song timing for tracks the headset brings itself (the format of GET /songs)
python tools/export_songs.py path/to/tracks/ -o ../unity/Assets/Songs/songs.json
```

`tools/make_test_wav.py --sloppy` makes an off-beat version for comparing metrics.

## Defaults chosen (all swappable)

| Piece | Default | Why | Env |
|---|---|---|---|
| Stack | Python 3.11+, FastAPI, one WebSocket per battle | numpy/Whisper ecosystem; Unity/Quest has solid WebSocket clients | |
| Transport | WebSocket, binary PCM16 mono 16 kHz + JSON events | simple, works through proxies; ~32 KB/s up | |
| Transcription | faster-whisper `small.en`, local | free, no key; GPU makes it ~real time, `base.en` for CPU. No GPU: `deepgram` (streaming, live partial words) or `openrouter` (a phrase at a time) | `TRANSCRIBER`, `WHISPER_MODEL`, `WHISPER_DEVICE`, `DEEPGRAM_API_KEY` |
| Rater | Jev via OpenRouter `POST /api/v1/systemone` | same key as the LLM; 70-500 ms typed answers | `RATER`, `JEV_BASE_URL`, `JEV_MODEL` |
| Opponent | OpenRouter `google/gemini-3.1-flash-lite`, reasoning off | ~2 s and ~$0.0006 per verse (Sonnet 5.5: ~12 s, ~10x the cost) | `OPPONENT`, `OPPONENT_MODEL` |
| Opponent voice | Qwen3-TTS 1.7B CustomVoice, speaker Ryan, local GPU (~4 GB) | follows per-line delivery directions the LLM writes (never shown to the user); each line time-stretched (WSOLA) to fill its bar. Alternatives: `breeze`, `indextts` (local services) or `openrouter` (hosted; see [../docs/SETUP.md](../docs/SETUP.md#cloud-mode-no-gpu-openrouter-for-everything)) | `VOICE`, `QWEN_TTS_MODEL`, `QWEN_TTS_SPEAKER`, `TTS_MODEL` |
| Fallbacks | heuristic rater, template opponent | runs with no keys; also used if a call fails or takes over 4 s | |

To call TypeSafe directly instead: `JEV_BASE_URL=https://api.typesafe.ai JEV_MODEL=jev-latest JEV_API_KEY=...`.
All of these can go in `backend/.env`.

## Beats

Drop songs (mp3/wav/ogg/flac/m4a) into `backend/songs/`. Where each bundled song is from, its license and credit line:
[songs/CREDITS.md](songs/CREDITS.md). At startup the server analyses new or changed
files (about a second each; results cached in `songs/beats.json`) and lists them at `GET /songs`; the
audio is served at `songs/files/<file>`. For each song (`freestyle/songs.py`):

- **Tempo:** the beat grid that best matches the song's onsets over 60-115 BPM, the range people rap at,
  so 140-150 BPM trap and drill productions come out at half time (72-75), the way they're rapped over.
  Refined to ~0.002 BPM; measured drift over a whole song is at most 6 ms.
- **Downbeat:** which beat starts the bar, from kick on 1 (and 3) and snare on 2 and 4.
- **Timing:** the grid is re-timed against the drum attacks (short-window energy), since the onset detector
  places them ~30 ms late. In the browser the drums land within 2-8 ms of the grid.
- **Loop:** whole bars from the first downbeat to where the audio fades, so long battles loop seamlessly.

The web GUI's Beat menu picks a song (its BPM is fixed) or the metronome. Use headphones: a beat playing
into the mic throws off the transcript and the flow meter.

## Opponents: character sheets

Each MC is a character sheet in `backend/freestyle/characters/<id>.toml` (copy `_TEMPLATE.toml`;
restart the server to load changes). A sheet has the MC's background, persona, traits, the topics they
rap about, catchphrases, outfit, sore spots, voice (Qwen speaker, a description Breeze designs the voice from, a voice
style, extra ad-libs; cloud voice IDs live in `backend/voices.toml`), starting mood,
and five 1-10 stats that set the mechanics:

| Stat | Drives |
|---|---|
| rep | how stingy the crowd is with the player's bars (`crowd_offset`: rep 2 = +1.0, rep 10 = -1.67) |
| presence | the crowd's bonus for the MC's own bars (`crowd_home`) |
| composure | how far the player's bars move the MC (`opp_gain`), how fast they recover (`resilience`), when they choke (`choke_below`) |
| wordplay | how well the LLM writes them (basic / solid / sharp / elite) |
| flow | how cleanly their verse rides the beat, as the rater hears it |

A `[tuning]` table overrides any derived number. The whole sheet goes into the opponent LLM's prompt.
**Sore spots** are hidden from the player until hit: for every player chunk Jev answers which sore spot
(if any) it went after, judged by meaning ("your crown got snatched" hits "losing their championship
belt"), so describe them concretely; the offline rater uses the sheet's keywords. A hit adds annoyance
and costs confidence (scaled by composure), reveals the spot in the sheet, and the MC's next verse is
told to answer it, heated.

Mood shows in the writing: below the choke point the LLM is told how many bars should fall apart (weak
rhymes, a line that trails off, a bar of "uhh... yeah... hold up") with a "hesitant, stumbling" delivery,
which Jev then rates as weak bars; at high confidence it writes at the top of its skill.

## How ratings work

**Chunks.** The live transcript is cut into chunks: every 6 new words, or at a pause
(0.35 s of silence) once at least 2 words are in. Each chunk is rated as soon as it is
cut, so the crowd reacts mid-verse. 2.5 s of silence with no words is rated as a stall.
The verse's last words are rated when the turn ends.

**Two separate Jev ratings per chunk**, called in parallel (Jev's docs advise one factor
per question and combining in code). Both get the chunk, the verse so far, the other side's
last verse, the live flow and lyric metrics, and the current crowd level and opponent mood.

- Crowd: `reaction` scored on a 10-level rubric (dead silent ... raise the roof), scaled to 0-11, plus
  `punchline` and `fell_off` (probability true), and `flow` and `lyricism` on a 0-4 scale.
- Opponent: `intimidated`, `entertained`, `confident`, `annoyed`, `impressed` each scored
  0-4 (shown as 0-10), and a `gesture` choice for the avatar.

**Crowd level** = base + reaction. A chunk rated above the base pops the crowd straight
to that level (a likely punchline adds up to 1.5 more) and raises the base by 30% of the gap,
so good bars build a room that stays loud. A chunk rated below the base causes a small groan
and lowers the base by 12% of the gap. The pop fades with a 2 s half-life; dead air drains the
base by 0.25 levels per second. Confidence from Jev scales every move.

**Points come from the crowd**: each chunk scores (0.6 × its reaction + 0.4 × the crowd level)
× the seconds it covered, plus 10 × the punchline probability when that is over 0.6. Stalls
earn nothing. The opponent's verse is scored the same way. Then the player gets an edge: user points x1.3,
AI points x0.85 (`USER_POINTS`, `OPPONENT_POINTS` in `game.py`), since live freestyling is much harder than
the AI's written verse.

The crowd, mood and points rules run in the client. `game.py` is the reference version (the test client and the
tests use it); `unity/.../BattleRules.cs` and `web/rules.js` are ports, so a tuning change goes into all three.

**Assist (opt-in, `start.assist`):** live rhyme hints for the last word heard (common perfect rhymes, then
two-vowel slant rhymes; `hints.py`), three word drops per turn from the opponent's last verse (+1.5 crowd
levels each when used), and a kinder crowd (smaller drops for weak bars, slower dead-air drain, +0.6 on
every chunk). The client decides how to show the hints; the web GUI's Assist checkbox is a test harness.

**Opponent mood** eases each dimension 45% of the way toward each rating. When the user's turn
ends, the client's `verse` request gives the opponent LLM its five mood numbers, its overall mood, the crowd
level, the score, and the three user lines that rattled it most, and it is told to play that mood in its verse.

At about 1-2 k input tokens per call and Jev's listed $0.042 per million input tokens,
two calls per chunk cost well under a cent. Tuning constants are at the top of `game.py` (and its two
ports) and `session.py` (chunking).

## Status

Tested here: metrics on synthetic clips (syllable count exact; pocket 0.96 tight vs
0.36 sloppy), full battles over the WebSocket with and without a beat, both Jev ratings and
the crowd/opponent model against a mocked Jev, fallback when Jev or the LLM is unreachable.
Live Whisper (`small.en`, float16 on an RTX 5070, ~90 ms per streaming step) on TTS speech,
word-for-word, including the browser GUI end to end with the demo clip.

Jev works through OpenRouter's `/api/v1/systemone` with model id `jev-latest` (the
`typesafe/...` ids from the model list return "does not exist"); 135-195 ms per call. Jev allows
at most 10 levels per score question, so the crowd rubric is 0-9 and is stretched onto the 0-11
crowd scale. If Jev calls fail, every chunk falls back to the heuristic rater and the reaction feed
shows `heuristic-fallback`; set `RATER=heuristic` to skip it.
Mic capture in the GUI has not been tried on a real mic yet.

## GPU memory and speed

The voice runs through faster-qwen3-tts (Qwen3-TTS with CUDA graphs), one line at a time, and the verse is
streamed: each line is sent as soon as it is rendered, and the client starts the verse once the lines in
hand and the arrival rate cover the rest without a gap (ad-libs fill the wait).

| | RTX 5070 |
|---|---|
| Whisper small + Qwen 1.7B loaded | 5.4 GB |
| One verse line (bfloat16 or float16) | ~1.2 s, faster than a bar, so the verse starts right after the LLM |
| Startup (model + ad-libs for every MC voice) | ~34 s once downloaded |

Plain qwen-tts on an RTX 2070 Super took ~34 s per 8-bar verse (batches of 4); the CUDA-graph path removes
the per-step overhead that dominated there. Not yet measured on the 2070: if a line takes longer than a
bar, the verse just starts a few bars later (tested by slowing renders to 3.5 s/line at 90 BPM: start
moved to fit, all 8 lines on consecutive bars). Low-VRAM mode (below 10 GB, or `LOW_VRAM=1`) uses int8
Whisper and frees the GPU cache after each verse; Turing cards use float16 (`QWEN_DTYPE`). Run one server
per GPU.

## Next steps

1. Tune the rubric wording and `game.py` gains from more live battles.
2. Beat sync on a real Quest: the Unity client sends `offset_ms` from the audio clock; the output latency
   it assumes still needs checking on the headset.
3. Echo: the server already ignores mic words during the opponent turn.
