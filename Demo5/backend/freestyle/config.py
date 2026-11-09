"""Runtime configuration, read from environment variables.

Every external service is optional. With no keys set the server runs fully
offline: a heuristic rater stands in for Jev and a template opponent stands in
for the OpenRouter LLM.

Variables can also go in backend/.env (KEY=value per line); real environment
variables win over the file.
"""
from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

ENV_FILE = Path(__file__).resolve().parents[1] / ".env"


def _env(name: str, default: str = "") -> str:
    return os.environ.get(name, default).strip()


@dataclass(frozen=True)
class Settings:
    # One shared password for the whole server (freestyle/auth.py). Empty = no password: anyone who can reach
    # the server can use it, which is fine on localhost but not behind a tunnel.
    password: str = field(default_factory=lambda: _env("FREESTYLE_PASSWORD"))

    # Audio the client must send: mono PCM16 little-endian at this rate.
    sample_rate: int = 16_000

    # Transcription: "whisper" (faster-whisper, local, free), "deepgram" (hosted, streaming), "openrouter" (hosted,
    # a phrase at a time), "scripted" (reads a word-timing JSON, for tests) or "none".
    transcriber: str = field(default_factory=lambda: _env("TRANSCRIBER", "whisper"))
    whisper_model: str = field(default_factory=lambda: _env("WHISPER_MODEL", "small.en"))
    whisper_device: str = field(default_factory=lambda: _env("WHISPER_DEVICE", "auto"))
    whisper_compute_type: str = field(default_factory=lambda: _env("WHISPER_COMPUTE", "default"))
    scripted_transcript: str = field(default_factory=lambda: _env("SCRIPTED_TRANSCRIPT"))
    # TRANSCRIBER=openrouter: hosted speech-to-text, a phrase at a time (no GPU needed).
    stt_model: str = field(default_factory=lambda: _env("STT_MODEL", "openai/whisper-large-v3-turbo"))
    # TRANSCRIBER=deepgram: hosted streaming speech-to-text, partial words within a few hundred ms (no GPU needed).
    # Falls back to openrouter (above) when Deepgram can't be reached and there's an OpenRouter key.
    deepgram_api_key: str = field(default_factory=lambda: _env("DEEPGRAM_API_KEY"))
    deepgram_model: str = field(default_factory=lambda: _env("DEEPGRAM_MODEL", "nova-3"))
    deepgram_url: str = field(default_factory=lambda: _env("DEEPGRAM_URL"))   # a self-hosted Deepgram (or a test fake)
    # boost the turn's word drops and the MC's name (Nova-3 keyterm prompting, ~25% on top of the base price)
    deepgram_keyterms: bool = field(default_factory=lambda: _env("DEEPGRAM_KEYTERMS", "1").lower() in ("1", "true", "yes", "on"))

    # Rater: "jev" or "heuristic". "auto" picks jev when a key is present.
    rater: str = field(default_factory=lambda: _env("RATER", "auto"))
    # Jev is reachable through OpenRouter (System One endpoint) or TypeSafe directly.
    # On OpenRouter's systemone endpoint the id is plain "jev-latest" (the "typesafe/..." ids 400).
    jev_base_url: str = field(default_factory=lambda: _env("JEV_BASE_URL", "https://openrouter.ai/api"))
    jev_model: str = field(default_factory=lambda: _env("JEV_MODEL", "jev-latest"))
    jev_api_key: str = field(
        default_factory=lambda: _env("JEV_API_KEY") or _env("OPENROUTER_API_KEY") or _env("TYPESAFE_API_KEY")
    )

    # Opponent: "openrouter" or "template". "auto" picks openrouter when a key is present.
    opponent: str = field(default_factory=lambda: _env("OPPONENT", "auto"))
    openrouter_api_key: str = field(default_factory=lambda: _env("OPENROUTER_API_KEY"))
    # Fast and cheap: ~2 s and well under a tenth of a cent per verse. "anthropic/claude-haiku-5.5" is
    # cheaper still but ~3.6 s; Sonnet writes a little better at ~10x the cost and ~12 s.
    opponent_model: str = field(default_factory=lambda: _env("OPPONENT_MODEL", "google/gemini-3.1-flash-lite"))

    # Opponent voice: "qwen" (Qwen3-TTS on the GPU, follows the LLM's delivery directions), "breeze"
    # (Breeze TTS 2 as a separate service, ./run_breeze.sh; ~7.7 GB VRAM, replaces Qwen), "none"
    # (the page picks a browser/espeak voice). "auto" uses qwen when torch sees a GPU.
    voice: str = field(default_factory=lambda: _env("VOICE", "auto"))
    qwen_tts_model: str = field(default_factory=lambda: _env("QWEN_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"))
    qwen_tts_speaker: str = field(default_factory=lambda: _env("QWEN_TTS_SPEAKER", "Ryan"))
    breeze_url: str = field(default_factory=lambda: _env("BREEZE_URL", "http://127.0.0.1:7860"))
    tts_model: str = field(default_factory=lambda: _env("TTS_MODEL", "elevenlabs/eleven-v3"))   # VOICE=openrouter
    indextts_url: str = field(default_factory=lambda: _env("INDEXTTS_URL", "http://127.0.0.1:7861"))
    breeze_cfg: float = field(default_factory=lambda: float(_env("BREEZE_CFG", "4.0")))  # instruction strength
    qwen_dtype: str = field(default_factory=lambda: _env("QWEN_DTYPE", "auto"))  # auto | bfloat16 | float16

    # Low-VRAM mode for 8 GB cards (e.g. RTX 2070): int8 Whisper, Qwen rendered 4 lines at a time,
    # GPU cache released after each render. "auto" turns it on below 10 GB of VRAM.
    low_vram: str = field(default_factory=lambda: _env("LOW_VRAM", "auto"))

    metrics_interval_s: float = 0.5

    def resolved_rater(self) -> str:
        if self.rater == "auto":
            return "jev" if self.jev_api_key else "heuristic"
        return self.rater

    def resolved_low_vram(self) -> bool:
        if self.low_vram.lower() in ("1", "true", "yes", "on"):
            return True
        if self.low_vram.lower() in ("0", "false", "no", "off"):
            return False
        try:
            import torch
            return torch.cuda.is_available() and torch.cuda.get_device_properties(0).total_memory < 10 * 1024**3
        except ImportError:  # no torch (no voice): judge by nvidia-smi instead
            import subprocess
            try:
                out = subprocess.run(["nvidia-smi", "--query-gpu=memory.total", "--format=csv,noheader,nounits"],
                                     capture_output=True, text=True, timeout=5).stdout.split()
                return bool(out) and int(out[0]) < 10 * 1024
            except (OSError, ValueError, subprocess.SubprocessError):
                return False

    def resolved_whisper_compute(self) -> str:
        if self.whisper_compute_type == "default" and self.whisper_device != "cpu" and self.resolved_low_vram():
            return "int8_float16"  # about half the VRAM of float16, small accuracy cost
        return self.whisper_compute_type

    def resolved_voice(self) -> str:
        return self.voice_decision()[0]

    def voice_decision(self) -> tuple[str, str]:
        """(voice, why), for the startup log."""
        if self.voice == "breeze":
            return "breeze", f"VOICE=breeze, Breeze TTS 2 service at {self.breeze_url}"
        if self.voice == "openrouter":
            if not self.openrouter_api_key:
                return "none", "VOICE=openrouter needs OPENROUTER_API_KEY"
            return "openrouter", f"VOICE=openrouter, {self.tts_model} on OpenRouter (no GPU)"
        if self.voice == "indextts":
            return "indextts", f"VOICE=indextts, IndexTTS-2.5 service at {self.indextts_url}"
        if self.voice != "auto":
            return self.voice, f"VOICE={self.voice} is set (backend/.env or environment)"
        try:
            import torch
        except ImportError:
            return "none", "PyTorch isn't installed (./setup.sh without --cpu/--no-voice installs it)"
        if not torch.cuda.is_available():
            return "none", f"PyTorch {torch.__version__} can't see a CUDA GPU (check nvidia-smi and the driver version)"
        return "qwen", f"GPU {torch.cuda.get_device_name(0)}"

    def resolved_opponent(self) -> str:
        if self.opponent == "auto":
            return "openrouter" if self.openrouter_api_key else "template"
        return self.opponent


def load_env_file(path: Path = ENV_FILE) -> None:
    if not path.exists():
        return
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip().strip("'\""))


def load() -> Settings:
    load_env_file()
    return Settings()
