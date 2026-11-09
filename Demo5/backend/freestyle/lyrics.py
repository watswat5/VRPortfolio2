"""Text metrics from the transcript: rhyme density, multisyllable rhymes, vocabulary.

Uses the CMU pronouncing dictionary (via `pronouncing`). Words it does not know
fall back to a spelling-based vowel tail, which is crude but keeps slang and
made-up words in play.
"""
from __future__ import annotations

import re
from dataclasses import asdict, dataclass

import pronouncing

_WORD = re.compile(r"[a-z']+")
_VOWEL_RUN = re.compile(r"[aeiouy]+[^aeiouy]*$")
STOPWORDS = frozenset(
    "a an the and or but i you he she it we they me my your to of in on at for is are was be "
    "im i'm it's its that this with so up yo like just".split()
)


def words(text: str) -> list[str]:
    return _WORD.findall(text.lower())


def _phones(word: str) -> list[str] | None:
    p = pronouncing.phones_for_word(word.strip("'"))
    return p[0].split() if p else None


def syllables(word: str) -> int:
    p = _phones(word)
    if p:
        return max(1, sum(ph[-1].isdigit() for ph in p))
    return max(1, len(re.findall(r"[aeiouy]+", word)))


def rhyme_key(word: str, vowels: int = 1) -> str | None:
    """Sound of the last `vowels` vowel nuclei plus the final consonants.

    Stress markers are dropped so 'flow'/'below' match, and for multisyllable
    keys only the vowel sequence is kept (assonance is how rap rhymes work).
    """
    p = _phones(word)
    if p:
        idx = [i for i, ph in enumerate(p) if ph[-1].isdigit()]
        if len(idx) < vowels:
            return None
        tail = p[idx[-vowels] :]
        if vowels == 1:
            return " ".join(ph.rstrip("012") for ph in tail)
        return " ".join(ph.rstrip("012") for ph in tail if ph[-1].isdigit())
    if vowels > 1:
        return None
    m = _VOWEL_RUN.search(word)
    return "~" + m.group(0) if m else None


@dataclass
class LyricMetrics:
    words: int
    syllables: int
    rhyme_density: float        # share of content words that rhyme with a nearby word
    multi_rhymes: int           # two-vowel (multisyllabic) rhyme pairs nearby
    end_rhyme_rate: float | None  # share of bar endings that rhyme with a recent bar ending
    unique_ratio: float         # distinct / total content words
    repeat_ratio: float         # share of words that repeat a 4-word phrase already said

    def as_dict(self) -> dict:
        return {k: (round(v, 3) if isinstance(v, float) else v) for k, v in asdict(self).items()}


def compute(tokens: list[str], bar_endings: list[str] | None = None, reach: int = 12) -> LyricMetrics:
    content = [w for w in tokens if w not in STOPWORDS]
    keys1 = [rhyme_key(w) for w in content]
    keys2 = [rhyme_key(w, 2) for w in content]
    rhymed = 0
    multi = 0
    for i, k in enumerate(keys1):
        if k is None:
            continue
        lo, hi = max(0, i - reach), min(len(content), i + reach + 1)
        if any(keys1[j] == k and content[j] != content[i] for j in range(lo, hi) if j != i):
            rhymed += 1
        k2 = keys2[i]
        if k2 and any(keys2[j] == k2 and content[j] != content[i] for j in range(lo, i)):
            multi += 1

    end_rate = None
    if bar_endings and len(bar_endings) >= 2:
        ek = [rhyme_key(w) for w in bar_endings]
        hits = sum(1 for i in range(1, len(ek)) if ek[i] and ek[i] in ek[max(0, i - 3) : i])
        end_rate = hits / (len(ek) - 1)

    grams = [tuple(tokens[i : i + 4]) for i in range(len(tokens) - 3)]
    seen: set[tuple] = set()
    repeated = 0
    for g in grams:
        if g in seen:
            repeated += 1
        seen.add(g)

    return LyricMetrics(
        words=len(tokens),
        syllables=sum(syllables(w) for w in tokens),
        rhyme_density=rhymed / len(content) if content else 0.0,
        multi_rhymes=multi,
        end_rhyme_rate=end_rate,
        unique_ratio=len(set(content)) / len(content) if content else 0.0,
        repeat_ratio=repeated / len(grams) if grams else 0.0,
    )
