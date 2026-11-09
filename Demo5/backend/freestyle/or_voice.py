"""The opponent's voice from a hosted TTS on OpenRouter (VOICE=openrouter): ElevenLabs v3 by default, or
Fish Audio Drama 3 (TTS_MODEL=fish-audio/drama-3).

Each MC needs a fixed voice ID for its provider (character sheet [voice] elevenlabs / fish): a voice
*description* alone gives a new voice on every line. Both models take the delivery as a bracketed
direction before the line, which they perform, not read (checked by transcribing the output). Without an
ID the MC's description goes in the brackets instead, with a warning. Lines are fitted to the bar like the
local voices; ad-libs are cached on disk per model and voice ID (the raw API audio too, so they are never paid for twice). No GPU needed.
"""
from __future__ import annotations

import asyncio
import hashlib
import io
import logging
import time

import httpx
import numpy as np

from .breeze_voice import SPEAKER_VOICE
from .qwen_voice import (ADLIB_CACHE, ADLIB_MAX_S, ADLIBS, FILL, HARD_END, STRETCH, _cap, adlib_cache_key, cache_get,
                         cache_put, clean_delivery, fit, to_wav, trim)

log = logging.getLogger("freestyle.or_voice")

URL = "https://openrouter.ai/api/v1/audio/speech"
# When a sheet has no ID for the provider: one fixed voice per Qwen speaker, so lines at least stay consistent.
DEFAULT_IDS = {
    "elevenlabs": {"Ryan": "pNInz6obpgDQGcFmaJgB", "Aiden": "TxGEqnHWrfWFTfGW9XjX"},   # Adam, Josh
    "fish": {"Ryan": "802e3bc2b27e49c2995d23ef70e6ac89", "Aiden": "802e3bc2b27e49c2995d23ef70e6ac89"},
}


class OpenRouterVoice:
    def __init__(self, api_key: str, model: str = "elevenlabs/eleven-v3", timeout_s: float = 20.0,
                 transport: httpx.BaseTransport | None = None, adlib_cache=ADLIB_CACHE):
        self.model = model
        self.name = f"OpenRouter {model}"
        self.family = {"fish-audio": "fish"}.get(model.split("/")[0], model.split("/")[0])   # sheet key for IDs
        self.client = httpx.Client(timeout=timeout_s, transport=transport,
                                   headers={"Authorization": f"Bearer {api_key}", "X-Title": "Freestyle Simulator"})
        self.adlib_cache = adlib_cache
        self.adlibs: dict[str, dict[str, bytes]] = {}
        self.model_ready = False
        self.line_s = 0.0

    # ---- voice per MC ---------------------------------------------------
    def voice_id(self, mc) -> str | None:
        ids = getattr(mc, "cloud_voices", {}) or {}
        if ids.get(self.family):
            return ids[self.family]
        return DEFAULT_IDS.get(self.family, {}).get(getattr(mc, "speaker", "Ryan"))

    def key_for(self, mc) -> str:
        return f"{mc.voice_key}|{getattr(mc, 'voice_description', '')}|{self.voice_id(mc)}"

    @staticmethod
    def _voice(mc) -> str:
        if mc is None:
            return SPEAKER_VOICE["Ryan"]
        desc = getattr(mc, "voice_description", "") or SPEAKER_VOICE.get(getattr(mc, "speaker", ""), SPEAKER_VOICE["Ryan"])
        style = getattr(mc, "voice_style", "").rstrip(".")
        return f"{desc}{'; ' + style if style else ''}"

    def _fetch(self, direction: str, text: str, voice_id: str | None = None) -> bytes:
        """The provider's mp3 for one request (this is the billed call)."""
        body = {"model": self.model, "input": f"[{direction}] {text}", "response_format": "mp3"}
        if voice_id:
            body["voice"] = voice_id
        r = self.client.post(URL, json=body)
        if "audio" not in (r.headers.get("content-type") or ""):
            raise RuntimeError(f"{self.model}: HTTP {r.status_code} {r.text[:200]}")
        return r.content

    @staticmethod
    def _decode(mp3: bytes) -> tuple[np.ndarray, int]:
        import soundfile as sf   # libsndfile decodes mp3
        x, sr = sf.read(io.BytesIO(mp3), dtype="float32", always_2d=True)
        return x.mean(axis=1), sr

    def _speech(self, direction: str, text: str, voice_id: str | None = None) -> tuple[np.ndarray, int]:
        return self._decode(self._fetch(direction, text, voice_id))

    def _raw_adlib(self, direction: str, text: str, voice_id: str | None) -> bytes:
        """The API's audio for an ad-lib, kept on disk under a key without ADLIB_CACHE_VERSION: changing how
        ad-libs are trimmed re-processes these files instead of paying for the renders again."""
        raw = self.adlib_cache / "raw" if self.adlib_cache else None
        key = hashlib.sha1("|".join((self.model, str(voice_id), direction, text)).encode()).hexdigest()[:20]
        p = raw / f"{key}.mp3" if raw else None
        if p and p.exists():
            return p.read_bytes()
        mp3 = self._fetch(direction, text, voice_id)
        if p:
            raw.mkdir(parents=True, exist_ok=True)
            p.write_bytes(mp3)
        return mp3

    # ---- lifecycle -----------------------------------------------------
    def load(self, voices: list | None = None) -> None:
        # Ready at once: verse lines only need the API. The ad-libs come after, each MC's as soon as it's done (a
        # battle against an MC whose ad-libs aren't in yet has the client voice its ad-libs): rendering them all takes
        # minutes on a fresh, throttled cloud instance with nothing cached, and battles shouldn't wait for that.
        self.model_ready = True
        log.info("%s voice ready; rendering ad-libs", self.name)
        t0 = time.perf_counter()
        self.render_adlibs(list({self.key_for(v): v for v in voices or []}.values()))
        log.info("%s ad-libs ready (%d voices in %.1f s)", self.name, len(self.adlibs), time.perf_counter() - t0)

    @property
    def ready(self) -> bool:
        return self.model_ready

    def release(self) -> None:
        pass

    def _who(self, mc) -> tuple[str | None, str]:
        """(voice ID, voice part of the direction): with an ID the voice is fixed and only the style goes in."""
        vid = self.voice_id(mc)
        if vid:
            return vid, (getattr(mc, "voice_style", "") or "").rstrip(".")
        log.warning("no %s voice ID for %s: its voice will change from line to line", self.family, getattr(mc, "name", "?"))
        return None, self._voice(mc)

    def render_adlibs(self, voices: list) -> None:
        for v in voices:
            vid, who = self._who(v)
            direction = "; ".join(x for x in (who, "hyped battle rapper ad-lib, confident, speak only the words") if x)
            out = {}
            for t in ADLIBS + list(getattr(v, "extra_adlibs", ())):
                key = adlib_cache_key(self.model, str(vid), direction, t)
                wav = cache_get(self.adlib_cache, key)
                if wav is None:
                    x, sr = self._decode(self._raw_adlib(direction, t, vid))
                    wav = to_wav(_cap(trim(x, sr=sr), sr, ADLIB_MAX_S), sr)
                    cache_put(self.adlib_cache, key, wav)
                out[t] = wav
            self.adlibs[self.key_for(v)] = out

    # ---- verse lines ---------------------------------------------------
    def render_line(self, line: str, delivery: str, bar_s: float, speaker: str | None = None, style: str = "",
                    mc=None) -> bytes:
        d = clean_delivery(delivery)
        flow = f"rapping in a rhythmic hip-hop flow at {4 * 60 / bar_s:.0f} BPM, on the beat, no pauses"
        vid, who = self._who(mc)
        direction = "; ".join(x for x in (who, d, flow) if x)
        target = bar_s * FILL
        t0 = time.perf_counter()
        x, sr = self._speech(direction, line, vid)
        x = trim(x, sr=sr)
        took = time.perf_counter() - t0
        if len(x) / sr > target * STRETCH[1] and took < 0.35 * bar_s:     # same second-take rule as the local voices
            y, sr2 = self._speech(f"{direction}; rapid-fire, fast-paced, words tightly packed", line, vid)
            y = trim(y, sr=sr2)
            if 0 < len(y) / sr2 < len(x) / sr:
                x, sr = y, sr2
        self.line_s = time.perf_counter() - t0
        return to_wav(_cap(fit(x, sr, target), sr, bar_s * HARD_END), sr)

    async def render_line_async(self, line: str, delivery: str, bar_s: float, speaker: str | None = None,
                                style: str = "", mc=None) -> bytes:
        return await asyncio.get_running_loop().run_in_executor(
            None, lambda: self.render_line(line, delivery, bar_s, speaker, style, mc))
