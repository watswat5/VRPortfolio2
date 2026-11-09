# Attribution checklist

Use this list to resolve assets whose source, creator, or license is missing or incomplete in the project notes. For each item, record the creator, original source URL, applicable license, and any required credit text in the project's credits file. If the source cannot be found, record that result and consider replacing the asset before redistribution.

There are no open items: every third-party and AI-generated asset below is attributed.

## Freestyle Simulator (`Demo5`)

Poster faces in `Demo5/art/posters/faces/` are in-engine character screenshots arranged into poster layouts; the poster work is attributed to Claude Opus 5.5 and documented in [Demo5/CREDITS.md](Demo5/CREDITS.md). They are not pending attribution items. The worn concrete floor texture and the app icon are ChatGPT-generated, and the spray-can rattle (Pixabay) and Oculus Lipsync (Meta) are credited there too.

The gesture-test and lip-sync-test clips under `Demo5/unity/Assets/GestureTest/Audio/` and `Demo5/unity/Assets/LipSyncTest/Audio/` were generated with the Qwen TTS option documented in `Demo5/backend/README.md` (Qwen3-TTS 1.7B CustomVoice). `Demo5/backend/web/clips/demo-verse.wav` is the robot’s eSpeak-generated test verse. These generated clips are documented in [Demo5/CREDITS.md](Demo5/CREDITS.md), so they are not pending attribution items.

The baked character clips under `Demo5/unity/Assets/Characters/*/Lines/` are treated as generated speech: they use the same TTS models as live generation, with voices matched to their respective characters. Exact per-file model versions and voice IDs are not recorded, but their source is known and they are not listed as unresolved assets here.

## Wi-Fi Heatmap (`Demo6`)

The ChatGPT concept images, the Meta Quest 3 and Touch Plus reference models (from Meta's developer site; the earlier Sketchfab controller scan was replaced and is no longer used), the XIAO board STEP, M5Stack's LEGO plate drawing and the three product reference photos are attributed in the Attributions table of [Demo6/README.md](Demo6/README.md), so they are not pending attribution items.

## Already documented

Music, Gregor Quendel's crowd sounds, the spray-can rattle, Oculus Lipsync, xkcd color names, Rocketbox avatars/animations, and the five hat models have attribution or license details in the project credits. Liberation Sans has its SIL Open Font License notice bundled with it. Project-generated CAD previews/exports and procedural art are not third-party source files; attribution for their external reference inputs is listed above where unresolved.
