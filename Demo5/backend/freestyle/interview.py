"""The pre-show interview: the interviewer's lines, hearing the player's answers, and summing them up.

The first time the player heads for the stage (no save file on the headset yet), an interviewer in a spotlit void asks
them a few questions. Her lines are pre-rendered (tools/render_interviewer.py, from LINES below): an intro, each
question, a generic affirmation after each answer, an outro. The headset records each answer and sends it here to be
heard (transcribe), then sends all of them to be summed up (summarize): a short profile of the player, written for
the opponents, who use it (session `start.profile`) to roast them with specifics. The headset keeps the summary in
its save file and skips the interview from then on.
"""
from __future__ import annotations

import json
import logging
import re

import numpy as np

log = logging.getLogger("freestyle.interview")

# id -> what she says. Questions are asked in order; an affirmation follows each answer (any of them, at random).
# Her register is a professional's (the suit): courteous, formal, a broadcast interviewer's, never slang.
INTRO = {"intro": "Good evening, and welcome. Before you take the stage, I have a few questions for tonight's "
                  "program. Please answer aloud, in your own words."}
QUESTIONS = {
    "q_name": "To begin: what name do you perform under?",
    "q_from": "Where are you from, and whom do you represent this evening?",
    "q_day": "And what do you do professionally, outside of competition?",
    "q_hero": "Which artist has influenced you the most?",
    "q_weak": "Strictly between us: is there a subject you would prefer your opponents not raise?",
    "q_last": "Finally, do you have a statement for your competitors?",
}
AFFIRMATIONS = {
    "ok_1": "Thank you.",
    "ok_2": "Very good.",
    "ok_3": "Understood.",
    "ok_4": "Noted.",
    "ok_5": "Excellent.",
    "ok_6": "I see.",
}
OUTRO = {"outro": "That concludes our interview. Thank you for your time, and best of luck this evening."}
LINES = {**INTRO, **QUESTIONS, **AFFIRMATIONS, **OUTRO}

MAX_ANSWERS = 12
MAX_ANSWER_CHARS = 400
MAX_SUMMARY_CHARS = 700


# ---- hearing an answer ---------------------------------------------------
_whisper = None


async def transcribe(settings, samples: np.ndarray, sr: int) -> str:
    """One answer, heard whole: the hosted speech-to-text when there's an OpenRouter key (TRANSCRIBER openrouter or
    deepgram), local Whisper for TRANSCRIBER=whisper, else nothing."""
    from . import transcribe as stt
    global _whisper
    if not len(samples):
        return ""
    if settings.transcriber in ("openrouter", "deepgram") and settings.openrouter_api_key:
        t = stt.OpenRouterTranscriber(sr, settings.openrouter_api_key, settings.stt_model)
    elif settings.transcriber == "whisper":
        _whisper = _whisper or stt.WhisperTranscriber(sr, settings.whisper_model, settings.whisper_device,
                                                      settings.resolved_whisper_compute())
        t = _whisper
    else:
        return ""
    t.begin_turn(0.0)
    t.push(samples.astype(np.float32))
    upd = await t.step(final=True)
    return " ".join(w.text for w in upd.committed + upd.partial).strip()


# ---- summing it up -------------------------------------------------------
def clean_answers(v) -> list[dict]:
    """[{"question", "answer"}] from the client: strings, one line each, cut to size."""
    out = []
    for a in v if isinstance(v, list) else []:
        if not isinstance(a, dict):
            continue
        q = " ".join(str(a.get("question", ""))[:200].split())
        ans = " ".join(str(a.get("answer", ""))[:MAX_ANSWER_CHARS].split())
        if q:
            out.append({"question": q, "answer": ans})
    return out[:MAX_ANSWERS]


SUMMARY_PROMPT = (
    "You prepare battle rappers' notes on their opponent. Below is the opponent's short pre-show interview (spoken "
    "answers, transcribed: they may be messy or joking). Write what a rival could use: their rap name, where they're "
    "from, what they do, who they look up to, what they're touchy about, and any brag or trash talk, as plain facts "
    "in 2 to 4 short sentences. Refer to them as \"they\" (never he or she). Skip anything they didn't say; don't "
    "invent. Reply with JSON only: {\"name\": \"<their rap name, or empty>\", \"summary\": \"<the notes>\"}"
)


def fallback_summary(answers: list[dict]) -> dict:
    """Without an LLM: the answers themselves, briefly."""
    said = [f"{a['question']} {a['answer']}" for a in answers if a["answer"]]
    name = next((a["answer"] for a in answers if "name" in a["question"].lower() and a["answer"]), "")
    return {"name": name[:40], "summary": " ".join(said)[:MAX_SUMMARY_CHARS], "source": "answers"}


async def summarize(settings, answers: list[dict], client=None) -> dict:
    """{"name", "summary", "source"}: the LLM's notes on the player (the opponent model on OpenRouter), else the
    answers themselves."""
    answers = clean_answers(answers)
    if not any(a["answer"] for a in answers):
        return {"name": "", "summary": "", "source": "empty"}
    if settings.resolved_opponent() != "openrouter":
        return fallback_summary(answers)
    import httpx
    own = client is None
    client = client or httpx.AsyncClient(base_url="https://openrouter.ai/api/v1", timeout=20.0,
                                         headers={"Authorization": f"Bearer {settings.openrouter_api_key}",
                                                  "X-Title": "Freestyle Simulator"})
    try:
        transcript = "\n".join(f"Q: {a['question']}\nA: {a['answer'] or '(no answer)'}" for a in answers)
        r = await client.post("/chat/completions", json={
            "model": settings.opponent_model, "temperature": 0.3, "reasoning": {"enabled": False},
            "messages": [{"role": "system", "content": SUMMARY_PROMPT}, {"role": "user", "content": transcript}]})
        r.raise_for_status()
        text = r.json()["choices"][0]["message"]["content"]
        m = re.search(r"\{.*\}", text, re.S)
        d = json.loads(m.group(0)) if m else {}
        summary = " ".join(str(d.get("summary", "")).split())[:MAX_SUMMARY_CHARS]
        if not summary:
            raise ValueError("no summary in the reply")
        return {"name": " ".join(str(d.get("name", "")).split())[:40], "summary": summary,
                "source": f"openrouter:{settings.opponent_model}"}
    except Exception as e:
        log.warning("interview summary failed, using the answers: %r", e)
        return fallback_summary(answers)
    finally:
        if own:
            await client.aclose()


def clean_profile(v) -> str:
    """The player's interview summary from the client (session `start.profile`): one paragraph, cut to size."""
    return " ".join("".join(c if c.isprintable() else " " for c in str(v or "")).split())[:MAX_SUMMARY_CHARS]
