# Running your own Freestyle server

The game needs its AI server (transcription, ratings, the MCs' lyrics and voices). A hosted one is linked from the
[project README](../README.md); this page is for running your own, from the `Demo5/` directory. The server's design,
tuning and options: [backend/README.md](../backend/README.md); its WebSocket protocol:
[backend/PROTOCOL.md](../backend/PROTOCOL.md). Hosting it on Google Cloud Run: [HOSTING.md](HOSTING.md).

The server also serves a browser version of the battle at `/` (play and test without the headset) and
`/markup.html`, for marking up each song's phrases and breaks by hand (turns follow them).

## Install and run (Linux, Python 3.11+)

```bash
./setup.sh            # .venv + dependencies; adds GPU extras when an NVIDIA GPU is present
$EDITOR backend/.env  # OPENROUTER_API_KEY=sk-or-...
./run.sh              # http://localhost:8000
```

**Password:** set `FREESTYLE_PASSWORD` in `backend/.env` before the server is reachable from outside (a tunnel,
port forwarding). Browsers then get a login page, and the Unity app and `tools/test_client.py --password -` ask
for it; nothing stores it. Without it the server is open to anyone who can reach it.

**Playing from another computer:** browsers only allow the mic on `https://` or `localhost`, so either
tunnel (`ssh -N -L 8000:localhost:8000 <server>`, then open `http://localhost:8000`) or start the server
with `HTTPS=1 ./run.sh` (self-signed certificate, accept the browser warning once; the headset then uses
`wss://`).

`./setup.sh --help` lists the options (`--no-voice`, `--cpu`, `--models`, `--rhubarb`: Rhubarb Lip Sync for the
opponent's mouth cues). Optional system packages: `sudo apt install espeak-ng` for the fallback voice.

| Piece | Runs | Needs |
|---|---|---|
| Speech-to-text | faster-whisper `small.en`, local | GPU recommended (CPU works with `base.en`) |
| Opponent voice | Qwen3-TTS 1.7B CustomVoice, local (or Breeze TTS 2, below) | NVIDIA GPU, ~4-5 GB VRAM |
| Opponent lyrics | OpenRouter (Gemini 3.1 Flash-Lite) | `OPENROUTER_API_KEY` |
| Crowd/opponent ratings | Jev via OpenRouter | same key |

Tests: `source .venv/bin/activate && cd backend && python -m pytest -q`.

## Optional: Breeze TTS 2 voice

[Breeze TTS 2](https://huggingface.co/BreezeBlue/Breeze-TTS-2) (BreezeBlue, 3B, research/non-commercial
license) can replace Qwen as the opponent's voice. It runs as its own service with its own venv, since it
pins different PyTorch/transformers versions, and needs ~7.7 GB of VRAM, so it's for 12 GB+ GPUs.

```bash
./setup_breeze.sh                       # code into third_party/, .venv-breeze, 7.7 GB of weights
echo "VOICE=breeze" >> backend/.env
./run_breeze.sh                         # the voice service on 127.0.0.1:7860 (plain mode; BREEZE_FAST=1 on 16 GB+ GPUs)
./run.sh                                # the game server, in another terminal
```

Each MC's voice is designed once from the `description` under `[voice]` in its character sheet (cached in
`backend/out/breeze_refs/`); every line then uses Breeze's voice direction with that reference plus the
LLM's delivery direction, and is fitted to the bar like the Qwen voice.

## Optional: IndexTTS-2.5 voice

[IndexTTS-2.5](https://huggingface.co/IndexTeam/IndexTTS-2.5) (bilibili, 0.8B) clones a reference voice and
takes an emotion direction as text. On an RTX 5070 it renders a verse line in ~2 s (under a bar even at
90 BPM) using ~7 GB of VRAM. Its `duration_factor` lets the model itself render each line at about the
bar's length (the service learns each voice's pace), so little time-stretching is left.

It can't design voices, so the MC voices come from Breeze's voice design once, then IndexTTS clones them:

```bash
./setup_indextts.sh                     # code + .venv-indextts + 5.5 GB of weights
./run_breeze.sh                         # once, in another terminal
source .venv/bin/activate && python backend/tools/make_voice_refs.py   # 6 voices in ~30 s; then stop Breeze
echo "VOICE=indextts" >> backend/.env
./run_indextts.sh                       # the voice service on 127.0.0.1:7861
./run.sh
```

## Cloud mode: no GPU (OpenRouter for everything)

```bash
./setup.sh --cpu                        # no PyTorch, no CUDA
TRANSCRIBER=openrouter VOICE=openrouter ./run.sh
```

Everything then runs on the one `OPENROUTER_API_KEY`, so the server fits on any small CPU host:

| Piece | Model (env) | Measured on a real battle |
|---|---|---|
| Speech-to-text | `openai/whisper-large-v3-turbo` (`STT_MODEL`) | a phrase's words arrive ~1.1-2.6 s after it ends (no live partial words) |
| Opponent voice | `elevenlabs/eleven-v3` (`TTS_MODEL`) | ~1.8 s per line, under a bar; verse starts one bar after the lyrics |
| Lyrics, ratings | Gemini 3.1 Flash-Lite, Jev | as before |

Roughly 2-3 cents per battle; capacity is limited by OpenRouter, not a GPU. Each MC's cloud voice is a
fixed voice ID, all kept in `backend/voices.toml` (`elevenlabs = "..."`; `fish = "..."` for
`TTS_MODEL=fish-audio/drama-3`, which is cheaper and faster; copy IDs from fish.audio/discovery):
a voice *description* alone gives a different voice on every line. Ad-libs are rendered once and cached.

### Optional: Deepgram for live words

OpenRouter's speech-to-text isn't streaming, so words (and the rhyme hints and word drops that follow them) lag a
phrase behind. [Deepgram](https://deepgram.com)'s streaming speech-to-text gives partial words within a few hundred
milliseconds. Add to `backend/.env`:

```bash
TRANSCRIBER=deepgram
DEEPGRAM_API_KEY=...      # console.deepgram.com: new accounts get free credit
```

Only the user's turns are streamed (a connection per turn), about a minute of audio per battle: roughly half a cent
with Nova-3 and keyterm prompting (the turn's word drops and the MC's name are boosted; `DEEPGRAM_KEYTERMS=0` turns it
off). If Deepgram can't be reached, the battle carries on with OpenRouter's speech-to-text. `DEEPGRAM_MODEL` picks the
model (default `nova-3`).
