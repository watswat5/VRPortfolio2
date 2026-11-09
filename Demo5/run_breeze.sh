#!/usr/bin/env bash
# Start the Breeze TTS 2 service the game server talks to (VOICE=breeze), on 127.0.0.1:7860.
#
# Default: plain (eager) mode, ~8 GB of VRAM, ~3.5-4.4 s per verse line on an RTX 5070.
#   BREEZE_FAST=1 ./run_breeze.sh            CUDA-graph speedups with a trimmed warmup profile (only cfg_scale 4,
#                                            prompts up to 256 tokens). Needs ~9.5 GB for Breeze alone: it ran out
#                                            of memory on a 12 GB card with a desktop using ~2 GB.
#   BREEZE_FAST=1 BREEZE_PROFILE=full ...    Breeze's full warmup profile (cfg 1 and 4, up to 512 tokens; big GPUs)
#   BREEZE_FLAGS="--fast-codec" ...          pick individual speedups by hand
set -euo pipefail
cd "$(dirname "$0")"
[ -d .venv-breeze ] && [ -d third_party/breeze-tts-2 ] || { echo "run ./setup_breeze.sh first" >&2; exit 1; }
export PYTORCH_CUDA_ALLOC_CONF=${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}
cd third_party/breeze-tts
if [ -n "${BREEZE_FLAGS:-}" ]; then
  read -r -a FAST <<< "$BREEZE_FLAGS"
elif [ "${BREEZE_FAST:-0}" = 1 ]; then
  FAST=(--fast-all)
else
  FAST=()
fi
PROFILE=configs/fast.json
if [ "${BREEZE_PROFILE:-trim}" = trim ]; then
  PROFILE=configs/fast_freestyle.json
  ../../.venv-breeze/bin/python - "$PROFILE" <<'PY'
import json, sys
p = json.load(open("configs/fast.json"))
p["name"] = "fast_freestyle"
p["service"]["cfg_scales"] = [4.0]                       # cfg > 1 runs as a batch of 2 (conditional + unconditional)
for name, stage in p["stages"].items():
    g = stage.get("graphs")
    if not isinstance(g, list):
        continue
    keep = []
    for x in g:
        if not isinstance(x, dict):
            keep.append(x); continue
        bs = x.get("batch_size", x.get("branch_batch_size"))
        ln = x.get("token_length", x.get("sequence_length", 0))
        if name == "codec" or (bs in (None, 2) and ln <= 256):
            keep.append(x)
    stage["graphs"] = keep
json.dump(p, open(sys.argv[1], "w"), indent=1)
PY
fi
exec ../../.venv-breeze/bin/python -c "
import sys
from pathlib import Path
import breeze_infer.api as api
api.FAST_CONFIG = Path('$PROFILE').resolve()
sys.argv = ['breeze_infer.api'] + sys.argv[1:]
api.main()" ../breeze-tts-2 --host 127.0.0.1 --port "${BREEZE_PORT:-7860}" "${FAST[@]}"
