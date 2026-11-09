"""The opponent's voice through a local speech service: Breeze TTS 2 (VOICE=breeze) or IndexTTS-2.5
(VOICE=indextts, backend/tools/indextts_server.py), as alternatives to Qwen3-TTS. Both speak the same
HTTP API; IndexTTS only clones voices, so its references come from Breeze's voice design
(backend/tools/make_voice_refs.py) and it gets the delivery direction and the bar length as their own
fields (emotion model, duration_factor).

Breeze runs as its own service (its own venv and pinned PyTorch/transformers; start it with
./run_breeze.sh) and is reached over HTTP: POST /v1/audio/speech returns mono 24 kHz PCM16.
VOICE=breeze in backend/.env selects it. It needs ~7.7 GB of VRAM, so it replaces Qwen on the GPU.

Consistent voices: each MC voice gets a reference clip once, made with Breeze's voice design from the
character sheet's voice description, cached in backend/out/breeze_refs/. Every verse line and ad-lib
is then rendered with voice direction: that reference plus the flow instruction and the LLM's
delivery direction. Lines are fitted to the bar exactly like the Qwen voice.
"""
from __future__ import annotations

import asyncio
import hashlib
import io
import logging
import time
import wave
from pathlib import Path

import httpx
import numpy as np

from .qwen_voice import (ADLIB_CACHE, ADLIB_MAX_S, ADLIB_STYLE, ADLIBS, FAST_STYLE, FILL, FLOW_STYLE, HARD_END, STRETCH,
                         _cap, adlib_cache_key, cache_get, cache_put, clean_delivery, fit, to_wav, trim)

log = logging.getLogger("freestyle.breeze_voice")

SR = 24_000
REFS = Path(__file__).resolve().parents[1] / "out" / "breeze_refs"
REF_TEXT = "Yo, check the mic, I'm about to take this stage and show everybody how it's done tonight."
SPEAKER_VOICE = {   # fallback voice description from the Qwen speaker, when a sheet has none
    "Ryan": "a confident young adult man with a punchy, rhythmic voice",
    "Aiden": "a younger man with a bright, energetic American voice",
}


class BreezeVoice:
    def __init__(self, url: str = "http://127.0.0.1:7860", cfg_scale: float = 4.0, timeout_s: float = 60.0,
                 transport: httpx.BaseTransport | None = None, ref_dir: Path = REFS, can_design: bool = True,
                 name: str = "Breeze TTS 2", adlib_cache: Path | None = ADLIB_CACHE):
        """can_design: the service can make a voice from a description (Breeze); otherwise (IndexTTS) the
        references must already exist in ref_dir."""
        self.url = url.rstrip("/")
        self.can_design, self.name = can_design, name
        self.adlib_cache = adlib_cache
        self.cfg_scale = cfg_scale
        self.client = httpx.Client(timeout=timeout_s, transport=transport)
        self.ref_dir = ref_dir
        self.refs: dict[str, bytes] = {}                  # voice key -> reference WAV
        self.adlibs: dict[str, dict[str, bytes]] = {}     # voice key -> text -> wav (same shape as QwenVoice)
        self.model = None                                 # set when ready
        self.sr = SR
        self.line_s = 0.0

    # ---- server --------------------------------------------------------
    def wait_for_server(self, max_s: float = 600.0) -> None:
        """The Breeze service loads its model separately; wait for it (e.g. started by ./run_breeze.sh)."""
        t0 = time.monotonic()
        while True:
            try:
                if self.client.get(f"{self.url}/health", timeout=5).status_code == 200:
                    return
            except httpx.HTTPError:
                pass
            if time.monotonic() - t0 > max_s:
                raise TimeoutError(f"no {self.name} server at {self.url} (start it with ./run_breeze.sh or ./run_indextts.sh)")
            time.sleep(3)

    def _speech(self, text: str, instruction: str | None, ref: bytes | None = None, seed: int = 42,
                delivery: str = "", target_s: float = 0.0) -> np.ndarray:
        data = {"text": text, "cfg_scale": str(self.cfg_scale), "seed": str(seed)}
        if delivery:
            data["delivery"] = delivery          # IndexTTS: text-to-emotion (Breeze ignores it)
        if target_s:
            data["target_seconds"] = f"{target_s:.3f}"   # IndexTTS: duration_factor toward this length
        if instruction:
            data["instruction"] = instruction
        files = None
        if ref is not None:
            data["ref_text"] = REF_TEXT
            files = {"ref_audio": ("ref.wav", ref, "audio/wav")}
        r = self.client.post(f"{self.url}/v1/audio/speech", data=data, files=files)
        r.raise_for_status()
        return np.frombuffer(r.content[: len(r.content) // 2 * 2], dtype="<i2").astype(np.float32) / 32768.0

    # ---- loading -------------------------------------------------------
    def design_references(self, voices: list) -> None:
        """Make (or load cached) reference clips for every voice; no ad-libs."""
        self.wait_for_server()
        for key, v in {self.key_for(v): v for v in voices}.items():
            self.refs[key] = self._reference(v)

    def load(self, voices: list | None = None) -> None:
        """voices: the MCs (voice_key, speaker, voice_style, voice_description, extra_adlibs)."""
        uniq = {self.key_for(v): v for v in voices or []}
        self.design_references(list(uniq.values()))
        t0 = time.perf_counter()
        self.render_adlibs(list(uniq.values()))
        self.model = self.url
        log.info("%s voice ready (%s, %d voices, ad-libs in %.1f s)", self.name, self.url, len(self.refs),
                 time.perf_counter() - t0)

    def _description(self, v) -> str:
        desc = getattr(v, "voice_description", "") or SPEAKER_VOICE.get(getattr(v, "speaker", ""), SPEAKER_VOICE["Ryan"])
        style = getattr(v, "voice_style", "")
        return f"A battle rapper: {desc}. {style}".strip()

    def _reference(self, v) -> bytes:
        """The MC's reference clip: designed once from its description, then cached on disk."""
        desc = self._description(v)
        name = hashlib.sha1(f"{desc}|{REF_TEXT}".encode()).hexdigest()[:16]
        path = self.ref_dir / f"{name}.wav"
        if path.exists():
            return path.read_bytes()
        if not self.can_design:
            raise RuntimeError(f"no voice reference for {getattr(v, 'name', '?')}: {self.name} can only clone. Design "
                               "the references once with Breeze: ./run_breeze.sh, then python backend/tools/make_voice_refs.py")
        seed = int(name[:8], 16) % 100000
        wav = to_wav(trim(self._speech(REF_TEXT, desc, seed=seed)), SR)
        self.ref_dir.mkdir(parents=True, exist_ok=True)
        path.write_bytes(wav)
        log.info("breeze reference for %s: %s", getattr(v, "name", "?"), path.name)
        return wav

    def render_adlibs(self, voices: list) -> None:
        """Ad-libs per voice, from the disk cache when possible (keyed by engine, reference, style, text)."""
        cache = self.adlib_cache
        for v in voices:
            ref = self.refs[self.key_for(v)]
            style = f"{ADLIB_STYLE} {getattr(v, 'voice_style', '')}".strip()
            mood = getattr(v, "voice_style", "") or "hyped, confident"
            ref_id = hashlib.sha1(ref).hexdigest()[:12]
            out = {}
            for t in ADLIBS + list(getattr(v, "extra_adlibs", ())):
                key = adlib_cache_key(self.name, ref_id, style, mood, t)
                wav = cache_get(cache, key)
                if wav is None:
                    wav = to_wav(_cap(trim(self._speech(t, style, ref, delivery=mood)), SR, ADLIB_MAX_S), SR)
                    cache_put(cache, key, wav)
                out[t] = wav
            self.adlibs[self.key_for(v)] = out

    @staticmethod
    def key_for(mc) -> str:
        """Breeze designs each voice from its description, so MCs only share a voice when that matches too."""
        return f"{mc.voice_key}|{getattr(mc, 'voice_description', '')}"

    @property
    def ready(self) -> bool:
        return self.model is not None

    def release(self) -> None:
        pass   # the model lives in the Breeze service

    # ---- generation ----------------------------------------------------
    def render_line(self, line: str, delivery: str, bar_s: float, speaker: str | None = None, style: str = "",
                    mc=None) -> bytes:
        """One verse line as a WAV fitted to its bar (voice direction from the MC's reference)."""
        ref = (self.refs.get(self.key_for(mc)) if mc is not None else None) or next(iter(self.refs.values()), None)
        flow = f"{FLOW_STYLE.format(bpm=4 * 60 / bar_s)} {style}".strip()
        d = clean_delivery(delivery)
        instruction = f"{flow} Delivery: {d}." if d else flow
        target = bar_s * FILL
        t0 = time.perf_counter()
        x = trim(self._speech(line, instruction, ref, delivery=d, target_s=target))
        took = time.perf_counter() - t0
        if len(x) / SR > target * STRETCH[1] and took < 0.35 * bar_s:   # same second-take rule as Qwen
            again = trim(self._speech(line, f"{instruction} {FAST_STYLE}", ref, delivery=d, target_s=target))
            if 0 < len(again) < len(x):
                x = again
        self.line_s = time.perf_counter() - t0
        return to_wav(_cap(fit(x, SR, target), SR, bar_s * HARD_END), SR)

    async def render_line_async(self, line: str, delivery: str, bar_s: float, speaker: str | None = None,
                                style: str = "", mc=None) -> bytes:
        return await asyncio.get_running_loop().run_in_executor(
            None, lambda: self.render_line(line, delivery, bar_s, speaker, style, mc))


def wav_seconds(wav: bytes) -> float:
    with wave.open(io.BytesIO(wav)) as f:
        return f.getnframes() / f.getframerate()
