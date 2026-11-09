"""IndexTTS-2.5 as a local speech service with the same API as Breeze's (VOICE=indextts).

Runs inside .venv-indextts (IndexTTS pins its own torch/transformers); start it with ./run_indextts.sh.

    GET  /health
    POST /v1/audio/speech   form: text, ref_audio (WAV file, required: IndexTTS clones voices, it can't
                            design them), delivery (emotion direction, optional), target_seconds (optional),
                            seed; instruction / ref_text / cfg_scale are accepted and ignored.
                            -> mono 24 kHz PCM16 (like Breeze)

Delivery directions go to IndexTTS's text-to-emotion model (use_emo_text). target_seconds is reached with
IndexTTS-2.5's duration_factor (the acoustic model renders the line at that length): the server learns
each voice's natural seconds per syllable from its own renders and picks the factor from that.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
import tempfile
import threading
import time
from pathlib import Path

import numpy as np
import torch
import torchaudio
import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, Response, UploadFile

ROOT = Path(__file__).resolve().parents[2]
REPO = ROOT / "third_party" / "index-tts"
sys.path.insert(0, str(REPO))

OUT_SR = 24_000
FACTOR_RANGE = (0.6, 1.4)
EMO_ALPHA = 0.6           # IndexTTS suggests ~0.6 for text-driven emotion; higher gets theatrical
app = FastAPI(title="IndexTTS-2.5 service")
tts = None
lock = threading.Lock()   # one generation at a time on the GPU
sec_per_syl: dict[str, float] = {}   # voice (ref hash) -> natural seconds per syllable at duration_factor 1
REFS = Path(tempfile.gettempdir()) / "indextts_refs"


def syllables(text: str) -> int:
    return max(1, sum(len(re.findall(r"[aeiouy]+", w)) or 1 for w in re.findall(r"[a-z']+", text.lower())))


@app.get("/health")
def health():
    if tts is None:
        raise HTTPException(503, "loading")
    return {"ok": True, "model": "IndexTTS-2.5"}


@app.post("/v1/audio/speech")
async def speech(text: str = Form(...), ref_audio: UploadFile | None = File(None), delivery: str = Form(""),
                 target_seconds: float = Form(0.0), seed: int = Form(42), instruction: str = Form(""),
                 ref_text: str = Form(""), cfg_scale: float = Form(1.0)):
    if tts is None:
        raise HTTPException(503, "loading")
    if ref_audio is None:
        raise HTTPException(400, "IndexTTS clones a voice: ref_audio is required")
    data = await ref_audio.read()
    key = hashlib.sha1(data).hexdigest()[:16]
    REFS.mkdir(exist_ok=True)
    ref = REFS / f"{key}.wav"              # stable path: IndexTTS caches the prompt's features by path
    if not ref.exists():
        ref.write_bytes(data)
    n = syllables(text)
    factor = 1.0
    if target_seconds > 0 and key in sec_per_syl:
        factor = float(np.clip(target_seconds / (n * sec_per_syl[key]), *FACTOR_RANGE))
    kw = {}
    if delivery.strip():
        kw = dict(use_emo_text=True, emo_text=delivery.strip(), emo_alpha=EMO_ALPHA)
    with lock:
        torch.manual_seed(seed)
        t0 = time.perf_counter()
        sr, wav = tts.infer(spk_audio_prompt=str(ref), text=text, output_path=None, lang="EN",
                            duration_factor=factor, verbose=False, **kw)
        took = time.perf_counter() - t0
    x = torch.as_tensor(np.asarray(wav, dtype=np.float32).reshape(-1) / 32768.0)
    secs = len(x) / sr
    natural = secs / factor / n                                   # what this voice does at factor 1
    sec_per_syl[key] = natural if key not in sec_per_syl else 0.7 * sec_per_syl[key] + 0.3 * natural
    y = torchaudio.functional.resample(x, sr, OUT_SR).clamp(-1, 1)
    print(f"line {took:.2f}s for {secs:.2f}s audio (factor {factor:.2f}, target {target_seconds:.2f}s)", flush=True)
    return Response((y.numpy() * 32767).astype("<i2").tobytes(), media_type="audio/pcm",
                    headers={"X-Render-Seconds": f"{took:.3f}", "X-Duration-Factor": f"{factor:.3f}"})


def main():
    global tts
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=7861)
    ap.add_argument("--no-bf16", action="store_true")
    a = ap.parse_args()
    os.chdir(REPO)                                  # IndexTTS resolves checkpoints/ relative paths from here
    from indextts.infer_v2_5 import IndexTTS2
    bf16 = not a.no_bf16 and torch.cuda.is_available() and torch.cuda.is_bf16_supported()
    # use_cuda_kernel=False: the fused BigVGAN kernel needs the CUDA toolkit (CUDA_HOME, nvcc) to compile; the plain
    # PyTorch vocoder is what the ~2 s/line measurement used.
    tts = IndexTTS2(cfg_path="checkpoints/config.yaml", model_dir="checkpoints", use_bf16=bf16, use_qwen_emo=True,
                    use_cuda_kernel=False)
    print(f"IndexTTS-2.5 ready (bf16={bf16})", flush=True)
    uvicorn.run(app, host=a.host, port=a.port, log_level="warning")


if __name__ == "__main__":
    main()
