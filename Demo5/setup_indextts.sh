#!/usr/bin/env bash
# Optional: IndexTTS-2.5 as the opponent's voice (VOICE=indextts). Its own venv (.venv-indextts, Python 3.11,
# pinned torch 2.8 / transformers 4.52) and ~5.5 GB of weights (+ ~3 GB of helper models fetched on first start).
# It clones voices, so design the MC references once with Breeze (./setup_breeze.sh, ./run_breeze.sh, then
# python backend/tools/make_voice_refs.py). License: bilibili Model Use License (commercial use by agreement).
set -euo pipefail
cd "$(dirname "$0")"
PY=${PYTHON:-python3.11}
mkdir -p third_party
[ -d third_party/index-tts ] || git clone --depth 1 https://github.com/index-tts/index-tts.git third_party/index-tts
[ -d .venv-indextts ] || "$PY" -m venv .venv-indextts
.venv-indextts/bin/python -m pip install -q --upgrade pip
.venv-indextts/bin/python -m pip install -q -e third_party/index-tts fastapi "uvicorn>=0.30" python-multipart
echo "==> downloading IndexTeam/IndexTTS-2.5 (5.5 GB) to third_party/index-tts/checkpoints"
.venv-indextts/bin/python -c "
from huggingface_hub import snapshot_download
snapshot_download('IndexTeam/IndexTTS-2.5', local_dir='third_party/index-tts/checkpoints')"
echo "Done. Start it with ./run_indextts.sh and set VOICE=indextts in backend/.env"
