# Freestyle Simulator: Unity client

The Unity client is the immersive game for the Freestyle Simulator. It targets Android/Meta Quest through Unity **6000.3.22f1**, URP and OpenXR. The active build scene is `Assets/Scenes/Venue.unity`; `SampleScene` is the unused Unity template, and `GestureTest.unity` / `LipSyncTest.unity` are editor test scenes for character animation and lip sync. The client owns the room, player/avatar, onboarding, battle timing and scoring. The server in [`../backend`](../backend/) transcribes and rates the player's voice, writes the opponent's verse, and supplies speech when a server voice is enabled. Unity and the browser client use the same [WebSocket protocol](../backend/PROTOCOL.md).

## Current experience

The main flow is a first-person VR experience:

1. On a new save, the stage door sends the player to a pre-show interview. The interviewer asks questions aloud; answers are recorded and sent to the server for transcription and a player summary. The summary is saved locally and passed to opponents in later battles. If server transcription is unavailable, the original answers are kept.
2. The player enters the backstage dressing room, where they can adjust their robot appearance, try wearable accessories, and choose a beat on the wall-mounted song list. The settings menu includes height and arm calibration, seated mode, comfort vignette, rhyme/word-drop assist, floating words, server URL and password.
3. Going through the stage door starts a battle in the club venue. The player freestyles into the tracked microphone while the client follows the beat and song phrases. A crowd, DJ, opponent avatars, stage lighting, score display, turn monitor, word hints and reactions respond to the server and game rules. The MCs' faces show their mood and flash their gestures, and the battle answers the player's gestures: pointing at the MC and raising the roof.

The project includes a flat-screen debug HUD (F1) for editor development. It is not the main game UI. The scene also includes a stereoscopic dressing-room mirror, interactable wardrobe, graffiti/spray-paint system, character animation and lip sync, and ILDA laser shows with simulated smoke.

## Open and run

1. Open `Demo5/unity` in Unity **6000.3.22f1** and open `Assets/Scenes/Venue.unity`.
2. Start the Python server from the `Demo5` project directory with `./run.sh`, or use the test server at `https://freestyle.dimma-dome.com/` (password given out separately; controls in the [project README](../README.md#controls)).
3. In Play mode, use the in-scene Settings menu to set the server URL and, if configured, its password. Connect, then choose a beat from the dressing-room song list. On a fresh save, complete the interview before entering the venue.
4. In the editor, press **F1** to show or hide the debug HUD. Its **Test Clip** field can send a WAV in place of live microphone input; a WAV made by `backend/tools/make_test_wav.py` works with `TRANSCRIBER=scripted`.

For a Quest build, use the Android target and the enabled `Venue.unity` scene. Configure OpenXR for the target headset and authorize microphone access on first use. A server running on another computer must be reachable from the headset: use its LAN URL over HTTP on a trusted local network or a public HTTPS URL with a valid certificate. Self-signed HTTPS certificates are not accepted by the client. Enter server passwords in Settings; they are sent for requests and are not saved in the scene or player preferences.

## Scene setup and regeneration

The checked-in `Venue.unity` is the main configured scene. Unity editor commands under **Freestyle** rebuild or wire scene content; save the scene after running them:

- **Build Venue** regenerates `Assets/Venue/Venue.prefab`, the venue geometry under `Assets/Venue/Generated/`, and `Assets/Scenes/Venue.unity` from the builder's floor-plan constants. It writes a new scene with the VR player, opponent posters, stage move area and **Set Up Battle** already applied; the other scene commands below (interview void, DJ, spray table, glasses rack, paintable walls) have to be run again afterwards.
- **Build Character Prefabs** imports/builds the Rocketbox characters and their animation setup.
- **Build Interview Void** creates the off-site pre-show interview space and hooks its director to the stage door.
- **Set Up Battle** wires the battle coordinator, opponent lineup, crowd, lighting, monitors, song selector, turn cues, floating words and stage-door events into the open scene.
- **Set Up DJ** creates or refreshes the DJ setup.
- **Build VR Player** builds `Assets/Player/VRPlayer.prefab` (XR rig, robot avatar, mic); **Build Mic**, **Build Headphones** and **Build Rapper Animator** rebuild the mic and DJ headphones prefabs and the shared `Rapper.controller`.
- **Add Stage Move Area**, **Hang Opponent Posters**, **Make Dressing Room Walls Paintable**, **Build Spray Table** (with its boombox), **Build Boombox** and **Build Glasses Rack** add or remake one group in the open scene without touching anything else.
- **Make Selected Into Hats / Glasses / Spray Cans** and **Make Selected Double-Sided** turn selected models into wardrobe items.
- **Build Gesture Test** and **Build Lip Sync Test** regenerate the two test scenes.

Generated meshes/materials are outputs: edit their builder/source inputs rather than hand-editing `Assets/Venue/Generated/`. `BattleSetup` is undoable and preserves selected moved scene objects when run again. The configured scene already contains the generated environment, so these commands are for regenerating or repairing it.

## Unity systems

| Area | Main scripts | Responsibility |
|---|---|---|
| Battle | `Assets/Scripts/Battle/BattleCoordinator.cs`, `BattleRules.cs`, `BattleProtocol.cs`, `BattleSocket.cs` | Connect to the server, synchronize audio and turns, apply crowd/opponent/score rules, expose events and state. `BattleRules` mirrors `backend/freestyle/game.py`; keep the implementations aligned. |
| Stage presentation | `OpponentStage.cs`, `DJHost.cs`, `Crowd.cs`, `CrowdAudio.cs`, `Scoreboard.cs`, `TurnDisplay.cs`, `FloatingWords.cs`, `TurnCues.cs` | Control performers, crowd, score and turn feedback, and word/hint presentation. |
| Player and XR | `Assets/Scripts/Player/` | XR rig and locomotion, tracked microphone, robot avatar, comfort settings and in-world Settings UI. `FlatControls` supports non-headset editor use. |
| Onboarding and saves | `Assets/Scripts/Game/InterviewDirector.cs`, `SaveFile.cs`, `GameReset.cs` | Run the first-launch interview, store the player record locally, and reset game state. |
| Wardrobe and venue | `Assets/Scripts/Wardrobe/`, `Assets/Scripts/Venue/` | Grab and equip accessories, spray graffiti, interact with doors/song selection, and run the mirror, crowd, lasers and smoke. |
| Character animation | `Assets/Scripts/Avatar/` | Drive performer motion, gaze, gestures, robot face and lip sync: baked or server mouth cues (`CueLipSync`), else the audio itself, through Oculus Lipsync on the Quest (`VisemeTap`) or uLipSync in the editor (`MouthDriver` picks). `FaceExpression` puts each character's mood and gestures on its face (the Rocketbox ARKit and FACS blendshapes, layered over lip sync and blinks); `Performer` adds it at runtime to any face that has the shapes. |
| Player gestures | `Assets/Scripts/Gestures/`, `Assets/Scripts/Battle/BattleGestures.cs` | A generic, rule-based recognizer: `GestureRig` watches the head and controllers, and each gesture component (`PointGesture`, `HandsRaisedGesture`, `HeadGesture` for nods and shakes, `ApproachGesture`) fires an Inspector event (`On Recognized`, `On Ended`) and the C# events `Fired` and `Gesture.Recognized`, with a hold time, cooldown and optional target; **Fire (test)** in a component's menu tries the wiring without a headset. `BattleGestures` (added to the battle at runtime) wires pointing at the MC (the MC reacts by mood, the crowd "oohs", the MC gets intimidated or annoyed) and raising the roof (the crowd swells) during a battle. |

Important packages include XR Interaction Toolkit, OpenXR, Input System, URP, Newtonsoft JSON, TextMesh Pro and uLipSync. `Assets/Oculus/LipSync/` is a trimmed Oculus Lipsync 29.0.0 (runtime scripts and the Android ARM64 library only); with it, the battle asks the server for no mouth cues (`start.mouth: false`, the coordinator's **Lip Sync On Device**), which keeps Rhubarb off the cloud server's CPU. The flat-screen debug HUD is `BattleDebugHud.cs`; F1 toggles it.

## Battle timing and audio

**Clock.** The client uses `AudioSettings.dspTime` for scheduled playback and accounts for output latency. The server's microphone timeline (`t`, seconds since the first sample) maps to it as `dsp = micT0 + t`; `start.beat.offset_ms` places the beat on that timeline.

**Beat and songs.** A song starts **Count In Bars** before its drop (`drop_s`) and loops whole bars (`loop_start_s`..`loop_end_s`) using two scheduled `AudioSource`s. Without a song, the client generates a metronome. The song selector combines bundled local songs and songs from the server (`GET /songs`). Local clips and their analysis can be bundled independently of the server; create the matching JSON with `python backend/tools/export_songs.py path/to/tracks/ -o unity/Assets/Songs/songs.json`. Keep file names aligned and regenerate timing data if a track changes. Server songs are downloaded when a battle starts unless a local clip with the same file name is available. Use *Decompress On Load* or *Compressed In Memory*, not *Streaming*, for bundled beats.

**Microphone.** Unity captures mono microphone audio and sends 20 ms PCM16 frames throughout the battle; the server resamples as needed. Android requests microphone permission. The tracked in-game mic is attached to the player's hand.

**Turns.** Song turns follow phrase boundaries and avoid marked breaks, as described in [`PROTOCOL.md`](../backend/PROTOCOL.md#http). The player goes first after the count-in. The coordinator sends `listen` and ends the player turn at its planned phrase duration, when `EndTurn()` is called, or when a test clip completes. It requests an opponent verse sized to the remaining phrase; the opponent begins on a bar line once enough audio is ready. With the metronome, turns use **Bars Per Turn** from the next bar line.

**Rules.** `BattleRules` maps ratings into crowd reaction, opponent mood and score, updates the crowd over time, and selects the winner. After player ratings, the coordinator sends battle context back to the server for subsequent ratings.

**Opponent voice** (**Voice** setting):

| Option | Source |
|---|---|
| Server generator | Uses the server's configured voice engine (Qwen, IndexTTS, Breeze or cloud). Lines stream as `opponent_line`; the client schedules the verse to the beat. `VoiceEngineName` reports the engine. Falls back to Server Smooth if the server has no voice generator. |
| Server Smooth | eSpeak NG via `/tts?seconds=`, fitted to each line's duration. |
| Server Grid | eSpeak NG via `/tts?bpm=`, with words placed on the 16th-note grid. |
| Text Only | No opponent audio; battle events, subtitles and lip sync still run. |

The opponent can ad-lib on the beat while composing. Clips play from the **Opponent Voice Anchor**, normally placed at the avatar's head, through pooled `AudioSource`s.

## Scene integration API

Scene components subscribe to the coordinator; battle logic does not directly manipulate scene objects.

- **Events:** `StatusChanged`, `LobbyLoaded`, `PhaseChanged`, `Ready`, `StateChanged`, `CrowdMoodChanged`, `OpponentMoodChanged`, `TurnStarted`, `UserTurnLive`, `TranscriptChanged`, `MetricsUpdated`, `Reaction`, `Punchline`, `FellOff`, `OpponentGesture`, `SoreSpotHit`, `WordDropsReceived`, `WordDropUsed`, `HintsReceived`, `UserTurnEnded`, `OpponentVerseReceived`, `OpponentVerseScheduled`, `OpponentLineStarted`, `OpponentVerseFinished`, `OpponentAdlib`, `OpponentVoiceScheduled`, `Beat`, `BattleFinished` and `ServerError`. A handler exception is logged without stopping the battle.
- **Inspector UnityEvents:** user turn live, opponent turn, punchline, fell off, crowd level/mood, opponent gesture/line, ad-lib, beat and result.
- **Properties:** `Phase`, `Round`, `CurrentState`, `CrowdLevel`, `Rules`, `Result`, `LastMetrics`; beat timing (`Bpm`, `BeatIndex`, `BeatInBar`, `BeatPhase`, `Now`); turn state (`CountInBeatsLeft`, `UserTurnIsLive`, `UserTurnStart`, `UserTurnBars`, `CurrentBar`, `TurnBars`, `TurnProgress`); text/hints (`CommittedText`, `PartialText`, `WordDrops`, `WordDropsUsed`, `Hints`); opponent (`VerseLines`, `VerseLine`, `VerseWaiting`, `Interpretation`, `LastAdlib`, `OpponentName`); lobby (`Songs`, `Mcs`, `Health`, `VoiceEngineName`, `MicLevel`).
- **Methods:** `LoadLobby()`, `StartBattle()`, `EndTurn()`, `StopBattle()` and `SetBeatVolume()`.

The server password is provided through `BattleCoordinator.Password` at runtime and sent as a bearer token over HTTP/WebSocket. The Settings UI keeps it in memory only. The server URL and player preferences such as height, seated mode, assist and comfort settings are stored locally.

## Venue contents

`VenueBuilder.cs` creates a compact local club from its floor-plan constants: stage, curtains, bar, kitchen, backstage dressing room, lighting, PA, and doors. The stage includes the DJ table, player/opponent marks, confidence monitors and spots for performers. The dressing room contains the outfit mirror, accessory shelf, song selector and stage door. Picking a beat on the song selector previews it on a boombox on the spray table below (`Boombox.cs`; its model is made by `art/boombox/make_boombox.py`): 30 seconds from the drop, stopped by picking it again, a battle starting or a stage door. The preview goes through `BattleCoordinator.PreloadSong`, so a server song is already downloaded when the battle starts. The interview void is built separately by `InterviewVoid.cs`. Venue dimensions and design assumptions are documented alongside the builder constants; generated geometry is chunked for Quest lighting limits.

The player can adjust robot proportions and appearance; accessories are interactable and snap to body sockets. The outfit description is included in battle setup for the opponent's context. Graffiti surfaces accept the spray-can interaction. The spray cans are physical (they fall, roll and can be thrown, and go back to the table a few seconds after being let go) and sound like cans: `SprayCanSounds` rattles a simulated mixing ball when one is shaken, hisses while spraying and thunks when one lands. Its clips are in `Assets/Resources/SprayCan/`, made by `art/spraycan/make_sounds.py`. The stage uses crowd sprites/audio, a scoreboard, turn cues, floating words, lasers and simulated smoke.

## Attribution

See [the project credits](../CREDITS.md) for music, sounds, models, fonts, textures, generated artwork and remaining asset-source gaps. The repository's [`ATTRIBUTION_CHECKLIST.md`](../../ATTRIBUTION_CHECKLIST.md) tracks unresolved source/license items across both projects.

## Validation status

Complete editor battles have been run with a scripted transcriber, heuristic rater, template opponent and test audio, covering song and metronome timing, rules, word drops, opponent playback and result handling; password acceptance and rejection were exercised too. On a Quest 3, full battles have been played with the live mic against the hosted Cloud Run server (November 2026): the MCs' lip sync runs on the headset (`[LipSync] Oculus Lipsync on` in its log) with every line's clip fetched. The spray cans' physics and sounds were tuned from rendered simulations of the rattle and have yet to be tried on the headset.
