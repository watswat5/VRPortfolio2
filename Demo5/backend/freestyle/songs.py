"""Backing beats: tempo and bar grid for each song in backend/songs/.

Each song is analysed once (results cached in songs/beats.json, redone when the
file changes):

* tempo: the beat grid that best lines up with the song's onsets, searched over
  60-115 BPM, the range people rap at. Trap and drill beats produced at 140-150
  BPM come out at half time (72-75), which is how they are rapped over. The
  tempo is then refined to ~0.002 BPM so a 3-minute song stays on the grid.
* downbeat: which of the 4 beats starts the bar, from the usual hip-hop pattern:
  kick on 1 (and 3), snare on 2 and 4.
* drop: the first bar where the bass comes in after a quiet intro (bar 0 when there
  is no intro). Clients start the song so the count-in ends on it.
* loop: whole bars from the drop to where the audio fades out, so a battle longer
  than the song can loop it without leaving the grid (or replaying the intro).
* breaks: stretches inside the loop where the beat drops out (an interlude, a
  stripped-down refrain, a one-bar pause before the beat comes back), as whole bars.
  Clients don't start a turn in one: it waits for the beat to drop again.
* phrases: the slots turns are made of. The beat between breaks of 2+ bars is a
  section. One-bar drop-outs stay in it (rappers ride them) but end a phrase, and
  the grid starts again after them, where the song's phrasing does. Each run is
  split every 8 bars; a leftover under 4 bars joins the phrase before it. Sections
  under 4 bars hold no turn.

Marked songs: phrases and breaks marked by hand in the web GUI's song markup page
(/markup.html) are kept in songs/markup.json and replace the analysed ones. A marked
phrase is a whole turn (`marked: true` in the song tells clients so); the drop is the
first phrase, and the loop runs from it to the end of the last marked bar.

The server only analyses songs and serves them; a battle never sees them (it gets
the tempo and where the beat sits on the mic timeline, nothing else). Playing a song,
and timing turns to it, is the client's job.
"""
from __future__ import annotations

import json
import logging
import re
from pathlib import Path

import numpy as np

log = logging.getLogger("freestyle.songs")

SONGS = Path(__file__).resolve().parents[1] / "songs"
CACHE = SONGS / "beats.json"
MARKUP = SONGS / "markup.json"               # hand-marked phrases and breaks, by file name
EXTS = (".mp3", ".wav", ".ogg", ".flac", ".m4a")
SR, HOP = 22050, 256
RAP_RANGE = (60.0, 115.0)      # BPM people rap at; faster productions are felt at half time
BEATS_PER_BAR = 4
VERSION = 11                   # bump to re-analyse everything
BREAK_BASS = 0.65              # a bar is quiet when its bass is under this share of a typical bar's
BREAK_ONE_BAR = 0.35           # ...and a single quiet bar is a break only when the whole mix is this quiet too
PHRASE_BARS = 8                # turns are made of phrases this long (a leftover under MIN_PHRASE_BARS joins the last)
MIN_PHRASE_BARS = 4


def _grid(env: np.ndarray, fps: float, bpm: float, phases: int = 48) -> tuple[float, float]:
    """(score, phase in seconds) of the best beat grid at `bpm`: mean onset on the grid / mean onset."""
    period = 60.0 / bpm * fps
    n = int((len(env) - 1) / period)
    k = np.arange(n) * period
    best = (-1.0, 0.0)
    for ph in np.linspace(0, period, phases, endpoint=False):
        s = float(env[(ph + k).astype(int)].mean())
        if s > best[0]:
            best = (s, ph / fps)
    return best[0] / (float(env.mean()) + 1e-9), best[1]


def _band_env(S: np.ndarray, freqs: np.ndarray, lo: float, hi: float) -> np.ndarray:
    """Onset strength in one frequency band (positive spectral flux)."""
    band = S[(freqs >= lo) & (freqs < hi)]
    flux = np.maximum(0.0, np.diff(np.log1p(band), axis=1)).sum(axis=0)
    return np.concatenate([[0.0], flux])


def analyse(path: Path) -> dict:
    import librosa

    y, sr = librosa.load(path, sr=SR, mono=True)
    dur = len(y) / sr
    fps = sr / HOP
    env = librosa.onset.onset_strength(y=y, sr=sr, hop_length=HOP, aggregate=np.median)

    # Tempo: coarse scan over the rap range, then refine around the winner.
    coarse = [(_grid(env, fps, b)[0], b) for b in np.arange(RAP_RANGE[0], RAP_RANGE[1] + 0.01, 0.5)]
    score, bpm = max(coarse)
    fine = [(_grid(env, fps, b, phases=96)[0], b) for b in np.arange(bpm - 0.6, bpm + 0.6, 0.002)]
    score, bpm = max(fine)
    _, phase = _grid(env, fps, bpm, phases=192)
    beat = 60.0 / bpm

    # Downbeat: kick (<150 Hz) on beat 1 (and 3), snare (150-4000 Hz) on 2 and 4.
    S = np.abs(librosa.stft(y, n_fft=2048, hop_length=HOP))
    freqs = librosa.fft_frequencies(sr=sr, n_fft=2048)
    kick, snare = _band_env(S, freqs, 20, 150), _band_env(S, freqs, 150, 4000)
    times = phase + beat * np.arange(int((dur - phase) / beat))
    frames = np.clip((times * fps).round().astype(int), 0, len(kick) - 1)
    k, s = kick[frames], snare[frames]
    pos = np.arange(len(times)) % BEATS_PER_BAR
    km = np.array([k[pos == j].mean() for j in range(BEATS_PER_BAR)]) / (k.mean() + 1e-9)
    sm = np.array([s[pos == j].mean() for j in range(BEATS_PER_BAR)]) / (s.mean() + 1e-9)
    bar_scores = [km[j] + 0.5 * km[(j + 2) % 4] + sm[(j + 1) % 4] + sm[(j + 3) % 4] - sm[j] - sm[(j + 2) % 4]
                  for j in range(BEATS_PER_BAR)]
    j = int(np.argmax(bar_scores))
    bar = beat * BEATS_PER_BAR
    # The onset envelope above uses ~90 ms windows, which place onsets late. Re-time the grid against the
    # attacks themselves: rise in short-window (1.5 ms hop) energy, summed over every beat in the song.
    phase += _attack_shift(y, sr, phase, beat, int((dur - phase) / beat))
    first = (phase + j * beat) % bar                      # earliest downbeat in the file

    # Drop: intros (4 quiet bars before the drums, in some of these beats) would leave the count-in ending
    # before the beat kicks in. Found to the beat: the first beat whose own bass, and the next bar's, reach half
    # the song's typical level, when every beat before it stays under a quarter of it (otherwise no intro).
    # A drop also settles the bar grid: the beat comes in on beat 1, so the downbeat is re-anchored to it
    # (kick-on-1-and-3 alone can't tell beat 1 from beat 3).
    low = S[(freqs >= 20) & (freqs < 150)].sum(axis=0)    # bass *energy* (onsets would catch an intro's fill)
    beat_bass = []
    # Each beat's window covers only that beat: from just after it to just before the next one, since the
    # ~93 ms STFT smears a big kick into the neighbouring beats.
    lag = 0.05
    for k in range(int((dur - phase) / beat) - 1):
        a, e = int((phase + k * beat + lag) * fps), int((phase + (k + 1) * beat - lag) * fps)
        beat_bass.append(float(low[a:e].mean()))
    beat_bass = np.array(beat_bass)
    typical = float(np.median(beat_bass)) if len(beat_bass) else 0.0
    drop_beat = None
    for k in range(min(len(beat_bass) - BEATS_PER_BAR, 17 * BEATS_PER_BAR)):
        if beat_bass[k] >= 0.5 * typical and beat_bass[k:k + BEATS_PER_BAR].mean() >= 0.5 * typical:
            if k >= BEATS_PER_BAR and (beat_bass[:k] < 0.25 * typical).all():
                drop_beat = k
            break
    if drop_beat is not None:
        first = (phase + drop_beat * beat) % bar
        drop = phase + drop_beat * beat
    else:
        drop = first
    drop_bar = int(round((drop - first) / bar))

    # Loop: from the drop to the last whole bar before the audio fades out.
    rms = librosa.feature.rms(y=y, hop_length=HOP)[0]
    loud = np.nonzero(20 * np.log10(rms + 1e-9) > 20 * np.log10(rms.max() + 1e-9) - 30)[0]
    end = (loud[-1] + 1) / fps if len(loud) else dur
    bars = max(1, int((end - drop) / bar))
    breaks = _breaks(S, low, fps, drop, bar, bars)
    phrases = _phrases(breaks, drop, bar, bars)
    ranked = sorted(bar_scores, reverse=True)
    return {
        "bpm": round(bpm, 3), "beats_per_bar": BEATS_PER_BAR, "first_downbeat_s": round(first, 4),
        "drop_s": round(drop, 4), "drop_bar": drop_bar,
        "loop_start_s": round(drop, 4), "loop_end_s": round(drop + bars * bar, 4), "loop_bars": bars,
        "breaks": breaks, "phrases": phrases,
        "duration_s": round(dur, 2), "grid_score": round(score, 2),
        "downbeat_margin": round(ranked[0] - ranked[1], 2),   # how clearly beat 1 stood out
    }


def _breaks(S: np.ndarray, low: np.ndarray, fps: float, drop: float, bar: float, bars: int) -> list[list[float]]:
    """[[start_s, end_s], ...]: whole bars in the loop where the beat drops out; end_s is where it drops again.

    A bar is quiet when its bass is well under the loop's typical bar. Two or more quiet bars in a row are a
    break (an interlude or a stripped-down refrain); a single quiet bar only when the whole mix goes quiet too
    (a pause before the beat comes back), since some beats leave the bass out of every other bar as their groove.
    """
    full = S.sum(axis=0)
    bass, mix = [], []
    for k in range(bars):   # each bar's window stops short of its edges (the STFT smears the next bar's kick)
        a, e = int((drop + k * bar + 0.05) * fps), int((drop + (k + 1) * bar - 0.05) * fps)
        bass.append(float(low[a:e].mean()) if e > a else 0.0)
        mix.append(float(full[a:e].mean()) if e > a else 0.0)
    bass, mix = np.array(bass), np.array(mix)
    if not len(bass) or np.median(bass) <= 0:
        return []
    quiet = bass < BREAK_BASS * np.median(bass)
    out, k = [], 0
    while k < bars:
        if not quiet[k]:
            k += 1
            continue
        j = k
        while j < bars and quiet[j]:
            j += 1
        if j - k >= 2 or mix[k] < BREAK_ONE_BAR * np.median(mix):
            out.append([round(drop + k * bar, 4), round(drop + j * bar, 4)])
        k = j
    return out


def _phrases(breaks: list[list[float]], drop: float, bar: float, bars: int) -> list[dict]:
    """[{"start", "end", "section"}, ...] in seconds: the loop's beat cut into turn-sized phrases (see the module doc)."""
    kind = ["beat"] * bars
    for a, b in breaks:
        ka, kb = round((a - drop) / bar), round((b - drop) / bar)
        for k in range(max(ka, 0), min(kb, bars)):
            kind[k] = "gap" if kb - ka >= 2 else "drop"
    sections, k = [], 0
    while k < bars:
        if kind[k] != "beat":       # gaps, and drop-outs that would start a section
            k += 1
            continue
        j = k
        while j < bars and kind[j] != "gap":
            j += 1
        if j - k >= MIN_PHRASE_BARS:
            sections.append((k, j))
        k = j
    out = []
    for si, (a, b) in enumerate(sections):
        # A drop-out ends a run of phrases and the 8-bar grid starts again after it, where the song's phrasing does.
        runs, start = [], a
        for k in range(a, b):
            if kind[k] == "drop" or k == b - 1:
                runs.append((start, k + 1))
                start = k + 1
        pieces: list[list[int]] = []
        for ra, rb in runs:
            if rb - ra < MIN_PHRASE_BARS and pieces:   # too short for a turn: joins the phrase before it
                pieces[-1][1] = rb
                continue
            run = [[c, min(c + PHRASE_BARS, rb)] for c in range(ra, rb, PHRASE_BARS)]
            if len(run) > 1 and run[-1][1] - run[-1][0] < MIN_PHRASE_BARS:
                last = run.pop()
                run[-1][1] = last[1]
            pieces += run
        out += [{"start": round(drop + s * bar, 4), "end": round(drop + e * bar, 4), "section": si} for s, e in pieces]
    return out


def _attack_shift(y: np.ndarray, sr: int, phase: float, beat: float, n: int, search: float = 0.06) -> float:
    """Seconds to add to the grid so beats sit on the drum attacks (+-search), from fine energy flux."""
    hop = 32
    frames = len(y) // hop
    e = np.log(1e-8 + (y[: frames * hop].reshape(frames, hop) ** 2).sum(axis=1))
    e = np.convolve(e, np.ones(8) / 8, mode="same")       # ~12 ms smoothing, keeps the attack edge
    flux = np.maximum(0.0, np.diff(e, prepend=e[0]))
    fps = sr / hop
    grid = phase + beat * np.arange(n)
    shifts = np.arange(-search, search, 1 / fps)
    score = [flux[np.clip(((grid + d) * fps).round().astype(int), 0, frames - 1)].mean() for d in shifts]
    return float(shifts[int(np.argmax(score))])


def _title(stem: str) -> str:
    """'jake_plah-trap-freestyle-beat-435149' -> 'Trap Freestyle Beat (jake_plah)'."""
    parts = stem.split("-")
    artist = parts[0] if len(parts) > 1 else ""
    words = [p for p in parts[1:] if not p.isdigit()] if artist else parts
    seen, out = set(), []
    for w in " ".join(words).replace("_", " ").split():
        if w.lower() not in seen:
            seen.add(w.lower())
            out.append(w.capitalize())
    return " ".join(out) + (f" ({artist})" if artist else "")


def describe(p: Path, analysis: dict) -> dict:
    """A song's entry as `GET /songs` lists it (without the url): its id, file and title, and the analysis."""
    return {"id": re.sub(r"[^a-z0-9]+", "-", p.stem.lower()).strip("-"), "file": p.name,
            "title": analysis.get("title") or _title(p.stem),
            **{k: v for k, v in analysis.items() if k != "key"}}


def load_markup(path: Path | None = None) -> dict:
    path = path or MARKUP
    try:
        return json.loads(path.read_text()) if path.exists() else {}
    except (OSError, json.JSONDecodeError) as e:
        log.warning("couldn't read %s: %r", path, e)
        return {}


def save_markup(file: str, markup: dict | None, path: Path | None = None) -> None:
    """Keep (or, with None, forget) one song's marks."""
    path = path or MARKUP
    data = load_markup(path)
    if markup is None:
        data.pop(file, None)
    else:
        data[file] = markup
    path.write_text(json.dumps(data, indent=1, sort_keys=True))


def apply_markup(song: dict, markup: dict | None) -> dict:
    """The song with its hand-marked phrases and breaks in place of the analysed ones.

    markup: {"first_downbeat_s" (optional: the bar grid, if it was moved), "segments": [{"kind": "phrase" | "break",
    "start", "end"}, ...]} in seconds on the bar grid. Phrases with no break between them share a section; gaps
    between phrases count as breaks; the loop runs from the first phrase to the end of the last segment."""
    if not markup:
        return song
    segs = sorted(((s["kind"], float(s["start"]), float(s["end"])) for s in markup.get("segments", [])
                   if s.get("kind") in ("phrase", "break") and float(s["end"]) > float(s["start"])), key=lambda s: s[1])
    phrases = [(a, b) for kind, a, b in segs if kind == "phrase"]
    if not phrases:
        return song
    bar = song.get("beats_per_bar", BEATS_PER_BAR) * 60.0 / song["bpm"]
    first = float(markup.get("first_downbeat_s", song["first_downbeat_s"]))
    out, section, prev = [], 0, None
    for a, b in phrases:
        if prev is not None and a > prev + 0.02:
            section += 1
        out.append({"start": round(a, 4), "end": round(b, 4), "section": section})
        prev = b
    breaks = [[a, b] for kind, a, b in segs if kind == "break"]
    for (_, e1), (s2, _) in zip(phrases, phrases[1:]):   # an unmarked gap between phrases is a break too
        if s2 > e1 + 0.02 and not any(a <= e1 + 0.02 and b >= s2 - 0.02 for a, b in breaks):
            breaks.append([e1, s2])
    start, end = phrases[0][0], max(b for _, _, b in segs)
    return {**song, "first_downbeat_s": round(first % bar, 4), "drop_s": round(start, 4),
            "drop_bar": int(round((start - first % bar) / bar)), "loop_start_s": round(start, 4),
            "loop_end_s": round(end, 4), "loop_bars": int(round((end - start) / bar)),
            "breaks": [[round(a, 4), round(b, 4)] for a, b in sorted(breaks)], "phrases": out, "marked": True}


def catalog(folder: Path = SONGS) -> list[dict]:
    """Every song with its grid; analyses new or changed files and updates the cache."""
    if not folder.is_dir():
        return []
    try:
        cache = json.loads(CACHE.read_text()) if CACHE.exists() else {}
    except (OSError, json.JSONDecodeError):
        cache = {}
    out, changed = [], False
    markup = load_markup()
    for p in sorted(f for f in folder.iterdir() if f.suffix.lower() in EXTS):
        st = p.stat()
        key = {"size": st.st_size, "mtime": int(st.st_mtime), "version": VERSION}
        entry = cache.get(p.name)
        if not entry or entry.get("key") != key:
            try:
                log.info("analysing beat %s", p.name)
                entry = {"key": key, **analyse(p)}
            except Exception as e:  # unreadable file: skip it, keep the rest
                log.warning("couldn't analyse %s: %r", p.name, e)
                continue
            cache[p.name] = entry
            changed = True
        song = describe(p, entry)
        out.append(apply_markup(song, markup.get(p.name)) if p.name in markup else song)
    if changed:
        try:
            CACHE.write_text(json.dumps(cache, indent=1, sort_keys=True))
        except OSError as e:
            log.warning("couldn't write %s: %r", CACHE, e)
    return out
