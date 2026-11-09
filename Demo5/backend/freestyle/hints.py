"""Freestyle assists: rhyme suggestions and word drops.

Rhymes: perfect rhymes first, then two-vowel slant rhymes (the assonance rap
actually runs on: "deliver" -> "quicker", "bigger"), only common words, most
common first. Word drops: a few words to work into the next verse, taken from
the opponent's last verse (to flip back) or from battle topics in round one.
"""
from __future__ import annotations

import random
import re
import threading

import pronouncing

from .lyrics import STOPWORDS

MIN_ZIPF = 3.0          # wordfreq scale: ~1 per million words; filters names and dictionary oddities
TOPICS = ["money", "crown", "fire", "mic", "crowd", "king", "street", "dream", "game", "throne", "gold", "storm",
          "ghost", "night", "chain", "ring", "beast", "fame", "rain", "snake", "boss", "smoke", "wave", "lion"]

# Not suggested: abbreviations and names that slip past the frequency filter, and crude words unless explicit.
SKIP = {"mr", "mrs", "ms", "dr", "st", "jr", "sr", "vs", "etc", "macleod", "missouri", "mciver", "pember"}
CRUDE = {"dick", "dicks", "pussy", "cock", "cunt", "whore", "slut", "bitch", "bitches", "fuck", "fucker", "fucking",
         "shit", "shitty", "ass", "asshole", "bastard", "damn", "hell", "piss", "tits", "fag", "nigga", "nigger"}

_index: dict[str, list[tuple[float, str]]] | None = None   # 2-vowel key -> [(zipf, word)], most common first
_zipf: dict[str, float] = {}
_lock = threading.Lock()


def _zipf_of(word: str) -> float:
    from wordfreq import zipf_frequency
    if word not in _zipf:
        _zipf[word] = zipf_frequency(word, "en")
    return _zipf[word]


def _vowels(phones: str, n: int) -> str | None:
    v = [p.rstrip("012") for p in phones.split() if p[-1].isdigit()]
    return " ".join(v[-n:]) if len(v) >= n else None


def warm() -> None:
    """Build the slant-rhyme index once (~2 s); call off the event loop."""
    global _index
    with _lock:
        if _index is not None:
            return
        pronouncing.init_cmu()
        idx: dict[str, list[tuple[float, str]]] = {}
        seen = set()
        for word, phones in pronouncing.pronunciations:
            if word in seen or not word.isalpha():
                continue
            seen.add(word)
            z = _zipf_of(word)
            key = _vowels(phones, 2)
            if key and z >= MIN_ZIPF:
                idx.setdefault(key, []).append((z, word))
        for v in idx.values():
            v.sort(reverse=True)
        _index = idx


def _ok(w: str, explicit: bool) -> bool:
    return len(w) >= 2 and w not in SKIP and (explicit or w not in CRUDE)


def rhymes(word: str, n: int = 10, explicit: bool = False) -> list[str]:
    """Common words that rhyme with `word`: perfect rhymes, then two-vowel slant rhymes."""
    word = word.lower().strip("'")
    if not word.isalpha():
        return []
    warm()
    perfect = sorted({w for w in pronouncing.rhymes(word)
                      if w.isalpha() and w != word and _ok(w, explicit) and _zipf_of(w) >= MIN_ZIPF},
                     key=lambda w: -_zipf_of(w))
    out = perfect[: max(n - 3, n // 2)]
    phones = pronouncing.phones_for_word(word)
    key = _vowels(phones[0], 2) if phones else None
    for _, w in (_index.get(key, []) if key else []):
        if len(out) >= n:
            break
        if w != word and w not in out and _ok(w, explicit) and not w.endswith(word) and not word.endswith(w):
            out.append(w)
    for w in perfect[len(out):]:          # still room: more perfect rhymes
        if len(out) >= n:
            break
        if w not in out:
            out.append(w)
    return out


def word_drops(opponent_verse: str = "", n: int = 3, rng: random.Random | None = None) -> list[str]:
    """Words to work into the next verse: from the opponent's last verse when there is one, else topics."""
    rng = rng or random.Random()
    pool = []
    for w in dict.fromkeys(re.findall(r"[a-z]+", opponent_verse.lower())):
        # specific enough to be worth flipping: not a stopword, not too common ("came"), not rare
        if len(w) >= 4 and w not in STOPWORDS and w not in CRUDE and 3.0 <= _zipf_of(w) <= 5.2 and pronouncing.phones_for_word(w):
            pool.append(w)
    picks = rng.sample(pool, min(n, len(pool)))
    picks += rng.sample([t for t in TOPICS if t not in picks], n - len(picks))
    return picks


def said(drop: str, words: list[str]) -> bool:
    """The drop word (or a plural/possessive of it) is among `words`."""
    return any(w == drop or w.rstrip("'s") == drop or w == drop + "s" or w == drop + "es" for w in words)
