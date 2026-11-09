"""Design each MC's reference voice with Breeze TTS 2 (from the [voice] description in its character sheet)
and cache it in backend/out/breeze_refs/, so voices that can only clone (VOICE=indextts) can use them.

    ./run_breeze.sh            (in another terminal; stop it afterwards to free the GPU)
    source .venv/bin/activate && cd backend && python tools/make_voice_refs.py
"""
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freestyle import config, mcs  # noqa: E402
from freestyle.breeze_voice import BreezeVoice  # noqa: E402

logging.basicConfig(level=logging.INFO, format="%(message)s")
s = config.load()
v = BreezeVoice(s.breeze_url, s.breeze_cfg)
v.design_references(mcs.ROSTER)
print(f"{len(v.refs)} voice references in {v.ref_dir}")
