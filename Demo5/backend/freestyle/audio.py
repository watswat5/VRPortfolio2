"""Incremental audio feature extraction.

Audio arrives as PCM16 chunks of any size. We keep a short tail of raw samples
and turn everything else into 10 ms frames of features, which is all the flow
metrics need. Memory stays small for long sessions (100 floats/s per feature).
"""
from __future__ import annotations

import numpy as np

HOP_S = 0.010
WIN_S = 0.025


def pcm16_to_float(data: bytes) -> np.ndarray:
    return np.frombuffer(data, dtype="<i2").astype(np.float32) / 32768.0


class FeatureTrack:
    """Per-frame RMS level and speech-band energy, appended as audio streams in."""

    def __init__(self, sample_rate: int):
        self.sr = sample_rate
        self.hop = int(sample_rate * HOP_S)
        self.win = int(sample_rate * WIN_S)
        self._pending = np.zeros(0, dtype=np.float32)
        self._window = np.hanning(self.win).astype(np.float32)
        freqs = np.fft.rfftfreq(self.win, 1.0 / sample_rate)
        # Vowel energy lives roughly in 300-2500 Hz; syllable nuclei show up as peaks here.
        self._band = (freqs >= 300) & (freqs <= 2500)
        self.rms_db: list[float] = []
        self.band_db: list[float] = []
        self.samples_seen = 0

    @property
    def n_frames(self) -> int:
        return len(self.rms_db)

    @property
    def duration_s(self) -> float:
        return self.samples_seen / self.sr

    def push(self, samples: np.ndarray) -> None:
        self.samples_seen += len(samples)
        buf = np.concatenate([self._pending, samples])
        n = 0 if len(buf) < self.win else 1 + (len(buf) - self.win) // self.hop
        if n:
            idx = np.arange(self.win)[None, :] + self.hop * np.arange(n)[:, None]
            frames = buf[idx] * self._window
            rms = np.sqrt(np.mean(frames**2, axis=1) + 1e-12)
            spec = np.abs(np.fft.rfft(frames, axis=1)) ** 2
            band = np.sum(spec[:, self._band], axis=1) / self.win
            self.rms_db.extend((20 * np.log10(rms + 1e-9)).tolist())
            self.band_db.extend((10 * np.log10(band + 1e-12)).tolist())
        self._pending = buf[n * self.hop :]

    def frame_time(self, i: int) -> float:
        return i * HOP_S + WIN_S / 2
