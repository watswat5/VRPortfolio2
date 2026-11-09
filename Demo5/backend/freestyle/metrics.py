"""Flow and performance metrics computed from audio features (and the beat grid).

All metrics are cheap numpy work over 10 ms frames, recomputed for a sliding
window and for the whole turn a couple of times a second.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass

import numpy as np

from .audio import HOP_S, FeatureTrack

MIN_PAUSE_S = 0.25      # silence shorter than this is just articulation
DEAD_AIR_S = 1.5        # silence longer than this reads as losing the flow
MIN_PEAK_GAP_S = 0.07   # ~14 syllables/s is about the human ceiling


@dataclass
class BeatGrid:
    bpm: float
    offset_s: float = 0.0       # time of any beat, on the audio timeline (first sample = 0)
    beats_per_bar: int = 4
    subdivision: int = 4        # grid slots per beat (4 = 16th notes)

    @property
    def beat_s(self) -> float:
        return 60.0 / self.bpm

    def phase(self, t: np.ndarray, per_beat: int | None = None) -> np.ndarray:
        """Position inside the current grid slot, in [0, 1)."""
        slot = self.beat_s / (per_beat or self.subdivision)
        return np.mod((t - self.offset_s) / slot, 1.0)

    def bar_index(self, t: float) -> int:
        return int(np.floor((t - self.offset_s) / (self.beat_s * self.beats_per_bar)))


@dataclass
class FlowMetrics:
    window_s: float
    speech_ratio: float          # share of the window with voice
    syllables: int
    syllable_rate: float         # syllables per second while rapping (pauses excluded)
    syllables_per_beat: float | None
    rate_consistency: float      # 1 = metronomic spacing, 0 = erratic
    pause_count: int
    longest_pause_s: float
    dead_air_s: float            # total silence in pauses > DEAD_AIR_S
    loudness_db: float | None    # median level of voiced frames
    dynamics_db: float           # spread of voiced levels (emphasis)
    on_beat: float | None        # share of syllables within 25% of a 16th-note slot
    pocket: float | None         # circular lock to the 16th grid, 0..1
    downbeat_hits: float | None  # share of beats that have a syllable on them

    def as_dict(self) -> dict:
        return {k: (round(v, 3) if isinstance(v, float) else v) for k, v in asdict(self).items()}


def _voiced_mask(rms_db: np.ndarray, floor_db: float) -> np.ndarray:
    threshold = max(floor_db + 12.0, -55.0)
    return rms_db > threshold


def _syllable_peaks(band_db: np.ndarray, voiced: np.ndarray) -> np.ndarray:
    """Indices of energy peaks that look like vowel nuclei."""
    if len(band_db) < 5:
        return np.zeros(0, dtype=int)
    k = np.ones(5) / 5
    env = np.convolve(band_db, k, mode="same")
    gap = int(MIN_PEAK_GAP_S / HOP_S)
    is_max = (env[1:-1] >= env[:-2]) & (env[1:-1] > env[2:])
    cand = np.nonzero(is_max)[0] + 1
    peaks: list[int] = []
    valley = env[0]
    last = -gap
    for i in cand:
        if not voiced[i]:
            continue
        lo = env[max(last, 0) : i + 1].min() if peaks else valley
        if env[i] - lo < 3.0:
            continue
        if i - last < gap:
            if env[i] > env[peaks[-1]]:
                peaks[-1] = i
                last = i
            continue
        peaks.append(i)
        last = i
    return np.array(peaks, dtype=int)


def _runs(mask: np.ndarray) -> list[tuple[int, int]]:
    """(start, length) of each True run."""
    if not len(mask):
        return []
    d = np.diff(np.concatenate([[0], mask.astype(np.int8), [0]]))
    starts = np.nonzero(d == 1)[0]
    ends = np.nonzero(d == -1)[0]
    return list(zip(starts.tolist(), (ends - starts).tolist()))


def compute(track: FeatureTrack, start_s: float, end_s: float, beat: BeatGrid | None) -> FlowMetrics:
    rms_all = np.asarray(track.rms_db, dtype=np.float32)
    floor = float(np.percentile(rms_all, 10)) if len(rms_all) else -90.0
    i0 = max(int(start_s / HOP_S), 0)
    i1 = min(int(end_s / HOP_S), len(rms_all))
    rms = rms_all[i0:i1]
    band = np.asarray(track.band_db[i0:i1], dtype=np.float32)
    window = max((i1 - i0) * HOP_S, 1e-6)
    voiced = _voiced_mask(rms, floor)
    voiced_s = float(voiced.sum() * HOP_S)

    peaks = _syllable_peaks(band, voiced)
    times = (peaks + i0) * HOP_S
    n = len(peaks)

    # Pauses: unvoiced runs between voiced audio (leading/trailing silence excluded).
    pauses = []
    voiced_idx = np.nonzero(voiced)[0]
    if len(voiced_idx):
        inner = ~voiced[voiced_idx[0] : voiced_idx[-1] + 1]
        pauses = [length * HOP_S for _, length in _runs(inner) if length * HOP_S >= MIN_PAUSE_S]
    # Trailing silence counts as dead air once it gets long (the rapper stopped).
    trailing = (len(voiced) - 1 - voiced_idx[-1]) * HOP_S if len(voiced_idx) else window
    dead = float(sum(p for p in pauses if p >= DEAD_AIR_S) + (trailing if trailing >= DEAD_AIR_S else 0.0))
    # Articulation time: from first to last syllable, minus the pauses inside.
    active_s = max(float(times[-1] - times[0]) - sum(pauses), 0.0) + 0.1 if n >= 2 else 0.0

    if n >= 3:
        ioi = np.diff(times)
        ioi = ioi[ioi < 0.6]  # gaps across pauses are not part of the cadence
        consistency = float(np.clip(1 - np.std(ioi) / (np.mean(ioi) + 1e-9), 0, 1)) if len(ioi) >= 2 else 0.0
    else:
        consistency = 0.0

    lv = rms[voiced]
    loud = float(np.median(lv)) if len(lv) else None
    dyn = float(np.percentile(lv, 90) - np.percentile(lv, 10)) if len(lv) > 10 else 0.0

    on_beat = pocket = downbeats = spb = None
    if beat is not None:
        spb = n / (active_s / beat.beat_s) if active_s > 0.3 else 0.0
        if n:
            ph = beat.phase(times)
            err = np.minimum(ph, 1 - ph)
            on_beat = float(np.mean(err < 0.25))
            pocket = float(np.abs(np.mean(np.exp(2j * np.pi * ph))))
            # Beats inside the voiced span that have a syllable within a 16th of them.
            first, last = times[0], times[-1]
            k0 = np.ceil((first - beat.offset_s) / beat.beat_s)
            k1 = np.floor((last - beat.offset_s) / beat.beat_s)
            grid = beat.offset_s + beat.beat_s * np.arange(k0, k1 + 1)
            if len(grid):
                tol = beat.beat_s / beat.subdivision / 2
                hit = np.min(np.abs(grid[:, None] - times[None, :]), axis=1) <= tol
                downbeats = float(np.mean(hit))

    return FlowMetrics(
        window_s=window,
        speech_ratio=voiced_s / window,
        syllables=n,
        syllable_rate=n / active_s if active_s > 0.3 else 0.0,
        syllables_per_beat=spb,
        rate_consistency=consistency,
        pause_count=len(pauses),
        longest_pause_s=max(pauses, default=0.0),
        dead_air_s=dead,
        loudness_db=loud,
        dynamics_db=dyn,
        on_beat=on_beat,
        pocket=pocket,
        downbeat_hits=downbeats,
    )


def trailing_silence_s(track: FeatureTrack) -> float:
    """Seconds since the last voiced frame (the whole stream if nobody spoke yet)."""
    rms = np.asarray(track.rms_db[-1000:], dtype=np.float32)
    if not len(rms):
        return 0.0
    floor = float(np.percentile(np.asarray(track.rms_db, dtype=np.float32), 10))
    voiced = np.nonzero(_voiced_mask(rms, floor))[0]
    return (len(rms) - 1 - voiced[-1]) * HOP_S if len(voiced) else len(rms) * HOP_S
