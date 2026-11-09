"""The opponent's voice: Qwen3-TTS CustomVoice, locally on the GPU.

The 1.7B CustomVoice model takes a natural-language speaking direction per
line ("aggressive, leaning in", "dismissive, cold"), which the opponent LLM
writes alongside its lyrics. The directions stay on the server. Directions that
ask for non-verbal sounds are cleaned up first: the model performs them
literally (random laughing).

Runs through faster-qwen3-tts (the same model with CUDA graphs), one line at a
time, so a battle can stream the verse: each line is sent as soon as it is
rendered. Each line is trimmed and time-stretched with WSOLA (pitch kept, far
less metallic on speech than a phase vocoder) to fill its bar. A line too long
to stretch that far gets a second, fast take when renders are quick enough,
and anything still over is cut with a short fade at the bar line, so a line
never runs into the next one. Ad-libs are rendered once at startup and cached.
"""
from __future__ import annotations

import asyncio
import hashlib
import io
import logging
import re
import threading
import time
import wave
from pathlib import Path

import numpy as np

log = logging.getLogger("freestyle.qwen_voice")

MODEL_ID = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"
SPEAKER = "Ryan"              # "dynamic male voice with strong rhythmic drive"
TOKENS_PER_S = 12             # the 12 Hz codec
FILL = 0.94                   # share of the bar a line should fill
HARD_END = 0.98               # nothing plays past this share of the bar
STRETCH = (0.8, 1.5)          # playback speed range for fitting; past it the line is re-rendered faster, then cut
FLOW_STYLE = ("Rap this line, don't read it: a rhythmic hip-hop flow at {bpm:.0f} BPM like a battle rapper on stage, "
              "steady cadence, words riding the beat with no pauses between them, punching the rhyming word at the end. "
              "Speak only the words: no laughing, sighing or other non-verbal sounds.")
FAST_STYLE = "Rapid-fire, fast-paced delivery, words tightly packed."
ADLIB_STYLE = ("Battle rapper hyping himself up between verses, confident, short ad-lib. "
               "Speak only the words, no laughing.")
ADLIB_CACHE = Path(__file__).resolve().parents[1] / "out" / "adlib_cache"
ADLIB_CACHE_VERSION = 3      # bump when trimming/capping changes, so cached ad-libs are re-rendered once
ADLIB_MAX_S = 1.6            # an ad-lib must leave room before the next bar
ADLIBS = ["my turn", "aight, my turn", "okay, okay, my turn", "uh huh", "yeah, yeah", "hold up", "okay",
          "check it", "you done?", "aight, aight", "mm hmm", "let's go", "watch this", "uh", "yeah"]
_NONVERBAL = re.compile(
    r"\b(half[- ]?|forced |nervous |little )?(laugh(s|ing|ed|ter)?|chuckl(e|es|ing|ed)|giggl(e|es|ing|ed)|cackl(e|es|ing|ed)|"
    r"snicker(s|ing|ed)?|scoff(s|ing|ed)?|snort(s|ing|ed)?|sigh(s|ing|ed)?|gasp(s|ing|ed)?|cough(s|ing|ed)?|"
    r"(heavy |deep )?breath(s|ing)?|groan(s|ing|ed)?|grunt(s|ing|ed)?|sniff(s|ing|ed)?|yawn(s|ing|ed)?|"
    r"whistl(e|es|ing|ed)|hum(s|ming|med)?|ha ?ha)\b", re.I)


class QwenVoice:
    def __init__(self, model_id: str = MODEL_ID, speaker: str = SPEAKER, dtype: str = "auto", low_vram: bool = False,
                 adlib_cache: Path | None = ADLIB_CACHE):
        """low_vram: release the GPU cache after each verse."""
        self.model_id, self.speaker = model_id, speaker
        self.dtype, self.low_vram = dtype, low_vram
        self.adlib_cache = adlib_cache
        self.model = None             # set when fully ready (model + ad-libs)
        self._model = None
        self.sr = 24_000
        self._lock = threading.Lock()   # one generation at a time on the GPU
        self.adlibs: dict[str, dict[str, bytes]] = {}   # voice key (mcs.MC.voice_key) -> text -> wav
        self.line_s = 0.0             # recent seconds to render one verse line (for logs and pacing)

    # ---- loading -------------------------------------------------------
    def load(self, voices: list | None = None) -> None:
        """voices: the MCs (anything with voice_key, speaker, voice_style, extra_adlibs) to pre-render ad-libs for."""
        import torch
        from faster_qwen3_tts import FasterQwen3TTS

        # bfloat16 where supported (Ampere and newer); Turing cards like the RTX 2070 only have float16.
        dtype = self.dtype if self.dtype != "auto" else "bfloat16" if torch.cuda.is_bf16_supported() else "float16"
        # faster-qwen3-tts runs the same model with CUDA graphs: far less per-step overhead, which is what
        # made the plain qwen-tts ~3x slower on an RTX 2070 than on newer cards.
        self._model = FasterQwen3TTS.from_pretrained(self.model_id, device="cuda", dtype=getattr(torch, dtype))
        self._generate(["warming up"], [ADLIB_STYLE], max_s=1.0)   # first call captures the CUDA graphs
        log.info("qwen %s on %s, low_vram=%s", dtype, torch.cuda.get_device_name(0), self.low_vram)
        self.render_adlibs(voices or [])
        self.model = self._model  # ready only once the ad-libs exist too
        log.info("qwen voice ready (%s, ad-libs for %d voices)", self.model_id, len(self.adlibs))

    def render_adlibs(self, voices: list) -> None:
        """Ad-libs per voice, from the disk cache when possible; only missing ones are generated."""
        cache = self.adlib_cache
        for v in {v.voice_key: v for v in voices}.values():
            texts = ADLIBS + list(v.extra_adlibs)
            style = f"{ADLIB_STYLE} {v.voice_style}".strip()
            keys = {t: adlib_cache_key(f"qwen|{self.model_id}", v.speaker, style, t) for t in texts}
            got = {t: cache_get(cache, keys[t]) for t in texts}
            missing = [t for t in texts if got[t] is None]
            if missing:
                raw = self._generate(missing, [style] * len(missing), max_s=[adlib_cap_s(t) for t in missing],
                                     speaker=v.speaker)
                for t, x in zip(missing, raw):
                    got[t] = to_wav(_cap(trim(x), self.sr, ADLIB_MAX_S), self.sr)
                    cache_put(cache, keys[t], got[t])
            self.adlibs[v.voice_key] = got
        self.release()

    @staticmethod
    def key_for(mc) -> str:
        """MCs that sound the same to this engine share ad-libs: same speaker, voice style and extra ad-libs."""
        return mc.voice_key

    @property
    def ready(self) -> bool:
        return self.model is not None

    def release(self) -> None:
        if self.low_vram:
            import torch
            torch.cuda.empty_cache()

    # ---- generation ----------------------------------------------------
    def _generate(self, texts: list[str], instructs: list[str], max_s: float | list[float],
                  speaker: str | None = None) -> list[np.ndarray]:
        """One text at a time (the CUDA-graph path has no batching). max_s: one cap, or one per text."""
        caps = max_s if isinstance(max_s, list) else [max_s] * len(texts)
        out: list[np.ndarray] = []
        with self._lock:
            for text, ins, cap in zip(texts, instructs, caps):
                wavs, sr = self._model.generate_custom_voice(
                    text=text, speaker=speaker or self.speaker, language="English", instruct=ins,
                    max_new_tokens=int(cap * TOKENS_PER_S) + 6)
                self.sr = sr
                out.append(np.asarray(wavs[0], dtype=np.float32))
        return out

    def render_line(self, line: str, delivery: str, bar_s: float, speaker: str | None = None, style: str = "",
                    mc=None) -> bytes:
        """One verse line as a WAV fitted to its bar."""
        flow = f"{FLOW_STYLE.format(bpm=4 * 60 / bar_s)} {style}".strip()
        d = clean_delivery(delivery)
        instruct = f"{flow} Delivery: {d}." if d else flow
        target = bar_s * FILL
        t0 = time.perf_counter()
        x = trim(self._generate([line], [instruct], max_s=bar_s * 1.6, speaker=speaker)[0])
        took = time.perf_counter() - t0
        # Too long to stretch into the bar: one more take told to go fast, but only when renders are quick
        # enough that the extra take can't make the line late (streaming plays a line per bar).
        if len(x) / self.sr > target * STRETCH[1] and took < 0.35 * bar_s:
            again = trim(self._generate([line], [f"{instruct} {FAST_STYLE}"], max_s=bar_s * 1.6, speaker=speaker)[0])
            if 0 < len(again) < len(x):
                x = again
        self.line_s = time.perf_counter() - t0
        return to_wav(_cap(fit(x, self.sr, target), self.sr, bar_s * HARD_END), self.sr)

    def render_verse(self, lines: list[str], deliveries: list[str], bar_s: float, speaker: str | None = None,
                     style: str = "") -> list[bytes]:
        """Every line at once (tests and tools); battles stream with render_line."""
        out = [self.render_line(l, d, bar_s, speaker, style) for l, d in zip(lines, deliveries)]
        self.release()
        return out

    async def render_line_async(self, line: str, delivery: str, bar_s: float, speaker: str | None = None,
                                style: str = "", mc=None) -> bytes:
        return await asyncio.get_running_loop().run_in_executor(
            None, self.render_line, line, delivery, bar_s, speaker, style)


# ---- audio helpers -----------------------------------------------------
def trim(x: np.ndarray, thresh: float = 0.01, sr: int = 24_000) -> np.ndarray:
    """Cut to the speech: start at the first real attack, end after the last sound.

    The start is where the 10 ms loudness first reaches 20 dB below the clip's peak (minus a 10 ms
    pre-roll), not the first sample above a fixed level, so a breath or quiet lead-in doesn't sit in
    front of the word. Clips are scheduled on the beat, so the word itself lands on it.
    """
    loud = np.nonzero(np.abs(x) > thresh)[0]
    if not len(loud):
        return x[:0]
    win = max(1, sr // 100)
    n = len(x) // win
    if n:
        rms = np.sqrt((x[: n * win].reshape(n, win) ** 2).mean(axis=1))
        attack = int(np.argmax(rms >= rms.max() * 0.1)) * win     # first 10 ms frame within 20 dB of the peak
        start = max(attack - win, 0)
    else:
        start = max(loud[0] - 240, 0)
    return x[start : loud[-1] + 480]


def adlib_cap_s(text: str) -> float:
    """Generation budget for an ad-lib: enough for its syllables, no spare second for a laugh after them."""
    syl = sum(len(re.findall(r"[aeiouy]+", w)) or 1 for w in re.findall(r"[a-z']+", text.lower()))
    return min(2.0, 0.5 + 0.3 * syl)


def adlib_cache_key(*parts: str) -> str:
    return hashlib.sha1("|".join([str(ADLIB_CACHE_VERSION), *parts]).encode()).hexdigest()[:20]


def cache_get(cache: Path | None, key: str) -> bytes | None:
    p = cache / f"{key}.wav" if cache else None
    return p.read_bytes() if p and p.exists() else None


def cache_put(cache: Path | None, key: str, wav: bytes) -> None:
    if cache:
        cache.mkdir(parents=True, exist_ok=True)
        (cache / f"{key}.wav").write_bytes(wav)


_AMUSED = re.compile(r"\b(amused|amusement|smirk\w*|grin\w*|playful\w*|tickled|giddy|gleeful\w*)\b", re.I)


def clean_delivery(d: str) -> str:
    """Drop requests for non-verbal sounds and words a TTS reads as "laugh here"; keep the tone as "smug"
    ("half laughing" -> "smug", "smirking at the opponent" -> "smug at the opponent")."""
    if not d:
        return d
    smug = bool(re.search(r"laugh|chuckl|giggl|cackl|snicker|ha ?ha", d, re.I)) or bool(_AMUSED.search(d))
    d = _NONVERBAL.sub("", d)
    d = _AMUSED.sub("smug", d)
    d = re.sub(r"\s+", " ", d)
    d = re.sub(r"\s*,(\s*,)*\s*", ", ", d).strip(" ,;")
    if smug and "smug" not in d:
        d = ", ".join(x for x in (d, "smug") if x)
    parts = [x.strip() for x in d.split(",") if x.strip()]
    return ", ".join(dict.fromkeys(parts))      # "smug, smug" -> "smug"


def fit(x: np.ndarray, sr: int, target_s: float) -> np.ndarray:
    """Time-stretch with WSOLA (pitch kept) toward `target_s`, within the allowed speed range."""
    if not len(x):
        return x
    from audiotsm import wsola
    from audiotsm.io.array import ArrayReader, ArrayWriter

    speed = min(max((len(x) / sr) / target_s, STRETCH[0]), STRETCH[1])
    if abs(speed - 1) < 0.03:
        return x
    out = ArrayWriter(1)
    wsola(1, speed=speed).run(ArrayReader(x[None, :]), out)
    return out.data[0].astype(np.float32)


def _cap(x: np.ndarray, sr: int, max_s: float, fade_s: float = 0.04) -> np.ndarray:
    """At most `max_s` long: speed up within range, then cut with a short fade."""
    if len(x) / sr <= max_s:
        return x
    x = fit(x, sr, max_s)
    n = int(max_s * sr)
    if len(x) <= n:
        return x
    x = x[:n].copy()
    f = int(fade_s * sr)
    x[-f:] *= np.linspace(1, 0, f, dtype=np.float32)
    return x


def to_wav(x: np.ndarray, sr: int) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    return buf.getvalue()
