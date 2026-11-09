#!/usr/bin/env bash
# Create .venv and install the Freestyle Simulator server.
#
#   ./setup.sh               everything: core + GPU extras (CUDA libs for Whisper, Qwen3-TTS voice)
#   ./setup.sh --no-voice    skip Qwen3-TTS / PyTorch (the page voices the opponent itself)
#   ./setup.sh --cpu         no GPU extras at all (Whisper on CPU, no Qwen voice)
#   ./setup.sh --models      also download the Whisper and Qwen models now, not on first start
#   ./setup.sh --rhubarb     also Rhubarb Lip Sync (mouth cues for the opponent's lines) into third_party/
#
# Re-running is safe: it reuses .venv and only adds what is missing.
set -euo pipefail
cd "$(dirname "$0")"

GPU=auto VOICE=1 MODELS=0 RHUBARB=0
for arg in "$@"; do
  case "$arg" in
    --cpu) GPU=0 ;;
    --no-voice) VOICE=0 ;;
    --models) MODELS=1 ;;
    --rhubarb) RHUBARB=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

PY=${PYTHON:-python3}
if ! "$PY" -c 'import sys; sys.exit(sys.version_info < (3, 11))' 2>/dev/null; then
  echo "Python 3.11+ is required (found: $("$PY" --version 2>&1)). Set PYTHON=/path/to/python3.x" >&2
  exit 1
fi
"$PY" -c 'import venv, ensurepip' 2>/dev/null || {
  echo "python3-venv is missing. On Ubuntu/Kubuntu: sudo apt install python3-venv" >&2; exit 1; }

if [ "$GPU" = auto ]; then
  if command -v nvidia-smi >/dev/null && nvidia-smi -L >/dev/null 2>&1; then
    GPU=1
  else
    GPU=0
    if command -v lspci >/dev/null && lspci | grep -qi 'vga.*nvidia\|3d.*nvidia'; then
      echo "!! An NVIDIA GPU is installed but its driver isn't working (nvidia-smi failed)." >&2
      echo "!! Install it (sudo ubuntu-drivers install, need 580+), reboot, then re-run ./setup.sh." >&2
      echo "!! Continuing with the CPU-only setup for now." >&2
    fi
  fi
fi

echo "==> venv (.venv)"
[ -d .venv ] || "$PY" -m venv .venv
PIP=".venv/bin/python -m pip"
C="-c backend/constraints.txt"   # known-good versions; keeps pip's resolver from dead-ending (Python 3.13/3.14)
$PIP install -q --upgrade pip

echo "==> core dependencies"
$PIP install -q $C -r backend/requirements.txt

if [ "$GPU" = 1 ]; then
  echo "==> GPU: $(nvidia-smi --query-gpu=name,memory.total --format=csv,noheader | head -1)"
  if [ "$VOICE" = 1 ]; then
    echo "==> GPU extras + Qwen3-TTS (PyTorch download is ~3 GB, this takes a while)"
    if ! $PIP install -q $C -r backend/requirements-gpu.txt; then
      echo "!! The Qwen voice extras failed to install (see pip's message above). Continuing without the voice:" >&2
      echo "!! the page voices the opponent itself. Installing the CUDA libs for Whisper only." >&2
      grep -E '^nvidia-' backend/requirements-gpu.txt | xargs $PIP install -q $C
    fi
  else
    echo "==> GPU extras (CUDA libs for Whisper only)"
    grep -E '^nvidia-' backend/requirements-gpu.txt | xargs $PIP install -q $C
  fi

  # faster-whisper finds the pip-installed CUDA libs through LD_LIBRARY_PATH; add it to activate once.
  if ! grep -q "cublas/lib" .venv/bin/activate; then
    NV=$(.venv/bin/python -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')/nvidia
    cat >> .venv/bin/activate <<EOF

# freestyle-cuda-libs: CUDA 12 cuBLAS/cuDNN wheels for faster-whisper (CTranslate2)
export LD_LIBRARY_PATH="$NV/cublas/lib:$NV/cudnn/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}"
EOF
  fi
else
  echo "==> no NVIDIA GPU: Whisper runs on CPU (set WHISPER_MODEL=base.en in backend/.env for speed), no Qwen voice"
fi

if [ ! -f backend/.env ]; then
  cp backend/.env.example backend/.env
  echo "==> created backend/.env: put your OPENROUTER_API_KEY in it"
fi
# No VOICE=none here: the server turns the Qwen voice off by itself when torch or a GPU is missing,
# so a later GPU install works without editing backend/.env.

command -v espeak-ng >/dev/null || \
  echo "note: espeak-ng not found; the 'Robot voice (espeak)' fallback needs it (sudo apt install espeak-ng)"

if [ "$MODELS" = 1 ]; then
  echo "==> downloading models"
  # shellcheck disable=SC1091
  source .venv/bin/activate
  (cd backend && python - <<'EOF'
from freestyle import config
s = config.load()
from faster_whisper import WhisperModel
WhisperModel(s.whisper_model, device="cpu", compute_type="int8")
print("whisper", s.whisper_model, "ok")
if s.resolved_voice() == "qwen":
    from huggingface_hub import snapshot_download
    snapshot_download(s.qwen_tts_model)
    print("qwen", s.qwen_tts_model, "ok")
EOF
  )
fi

if [ "$RHUBARB" = 1 ]; then
  RHUBARB_VERSION=1.14.0
  if [ -x "third_party/Rhubarb-Lip-Sync-$RHUBARB_VERSION-Linux/rhubarb" ]; then
    echo "Rhubarb $RHUBARB_VERSION already in third_party/"
  else
    echo "Rhubarb Lip Sync $RHUBARB_VERSION (87 MB, MIT) -> third_party/"
    mkdir -p third_party
    curl -fL -o third_party/rhubarb.zip \
      "https://github.com/DanielSWolf/rhubarb-lip-sync/releases/download/v$RHUBARB_VERSION/Rhubarb-Lip-Sync-$RHUBARB_VERSION-Linux.zip"
    unzip -q -o third_party/rhubarb.zip -d third_party && rm third_party/rhubarb.zip
  fi
fi

cat <<EOF

Done. Next:
  1. Put your OpenRouter key in backend/.env
  2. ./run.sh            (serves http://localhost:8000; the Quest connects to ws://<this-machine>:8000/ws/battle)
  3. Tests: source .venv/bin/activate && cd backend && python -m pytest -q
EOF
