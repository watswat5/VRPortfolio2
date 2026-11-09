#!/usr/bin/env bash
# Start the IndexTTS-2.5 service the game server talks to (VOICE=indextts), on 127.0.0.1:7861.
# Needs the MC voice references designed once with Breeze (backend/tools/make_voice_refs.py).
set -euo pipefail
cd "$(dirname "$0")"
[ -d .venv-indextts ] && [ -d third_party/index-tts/checkpoints ] || { echo "IndexTTS isn't set up (see README)" >&2; exit 1; }
export PYTORCH_CUDA_ALLOC_CONF=${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}
exec .venv-indextts/bin/python backend/tools/indextts_server.py --port "${INDEXTTS_PORT:-7861}" "$@"
