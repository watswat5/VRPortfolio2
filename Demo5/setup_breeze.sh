#!/usr/bin/env bash
# Optional: Breeze TTS 2 as the opponent's voice (VOICE=breeze). Separate from setup.sh because it pins its own
# PyTorch/transformers (it gets its own venv, .venv-breeze) and its weights are 7.7 GB. Needs ~8 GB of VRAM on top
# of Whisper, so it's for 12 GB+ GPUs, and it replaces Qwen. License: BreezeBlue Research and Non-Commercial.
#
#   ./setup_breeze.sh        clone the code into third_party/, build .venv-breeze, download the weights
set -euo pipefail
cd "$(dirname "$0")"
PY=${PYTHON:-python3}
mkdir -p third_party
if [ ! -d third_party/breeze-tts ]; then
  git clone --depth 1 https://github.com/breezeblue-ai/breeze-tts.git third_party/breeze-tts
fi
[ -d .venv-breeze ] || "$PY" -m venv .venv-breeze
.venv-breeze/bin/python -m pip install -q --upgrade pip
.venv-breeze/bin/python -m pip install -q -r third_party/breeze-tts/requirements.txt
echo "==> downloading BreezeBlue/Breeze-TTS-2 (7.7 GB) to third_party/breeze-tts-2"
.venv-breeze/bin/python -c "
from huggingface_hub import snapshot_download
snapshot_download('BreezeBlue/Breeze-TTS-2', local_dir='third_party/breeze-tts-2')"
grep -q '^VOICE=' backend/.env 2>/dev/null || echo "note: add VOICE=breeze to backend/.env to use it"
echo "Done. Start the voice with ./run_breeze.sh, then the game server with ./run.sh"
