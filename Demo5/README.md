# Freestyle Simulator

A VR freestyle rap battle for Meta Quest. You step onto a club stage with a mic in your hand and battle AI MCs over a
beat. They listen to every bar you rap, react to it with their faces and bodies, and fire back a comeback in their
own voice, while the crowd, a DJ host and a tug-of-war scoreboard show who's winning.

- **Virtual humans:** five MCs, a DJ who hosts the battles and an interviewer, on Microsoft Rocketbox characters with
  motion-capture animation, eyes that follow you, facial expressions that show their mood, and lip sync to their
  voices.
- **AI that listens and talks:** your rap is transcribed live and rated for punchlines, rhymes and flow; each MC
  writes a comeback about what you said (and what you told the interviewer), voiced in its own voice.
- **Gestures both ways:** the MCs laugh, glare, step back and hype; they answer yours: point at the MC to get in their
  head, raise the roof to hype the crowd.
- **Your avatar:** a hovering robot driven by your headset and controllers, dressed up in front of a dressing-room
  mirror (three bodies, two heads, hats, glasses, spray paint), holding your mic on stage. The spray cans are physical:
  they fall, roll and rattle when shaken.

## Video

https://youtu.be/J6tdRx6o9Ds

## Release

- **Quest APK:** https://github.com/ugavrclass2026/portfolio-2-watswat5/releases/tag/Alpha
- **Browser version** (the same battle on a phone or computer, no headset): https://freestyle.dimma-dome.com/
  (password required, see below)

## Running it

The game needs its AI server. A hosted one runs at `https://freestyle.dimma-dome.com/`; access requires a password. Contact the project owner for access.

1. Install the APK on a Quest 3 or 3S in developer mode: `adb install -r demo5.apk` (or Meta Quest Developer Hub
   / SideQuest). Allow the microphone when it asks.
2. Press the left controller's menu button. Under **SETTINGS** set **SERVER** to `https://freestyle.dimma-dome.com/`
   (with the `https://`), **PASSWORD** to the password, then **CONNECT**. Set your height there too, or **SEATED** if
   you're sitting.
3. In the dressing room, dress up at the mirror, pick a beat on the song list, and grab the stage door. The first time,
   a short interview comes first: answer out loud.
4. On stage the DJ introduces you both and counts you in. Rap when your light turns white and the monitor counts down;
   two rounds each, then the DJ announces the winner. Beat an MC to face the next one.

To run your own server (on a GPU, or on cloud APIs only): [docs/SETUP.md](docs/SETUP.md). How the hosted one is
deployed: [docs/HOSTING.md](docs/HOSTING.md). Opening the Unity project: [unity/README.md](unity/README.md).

## Controls

| Control | Does |
|---|---|
| Left menu button (☰) | Open or close the menu: settings, server, your player sheet |
| Trigger (pointing at a menu) | Click |
| Left stick | Walk (in the dressing room and on the stage) |
| Right stick | Snap turn |
| Grip | Grab: hats, glasses, spray cans, the stage door |
| Trigger (holding a spray can) | Spray paint (walls, posters, your robot) |
| Rap | Into the mic in your right hand; it's on whenever you're on stage |
| Point at the MC (arm out, hold) | Gets in their head: they react, the crowd goes "ooh" |
| Raise the roof (pump both hands overhead) | Hypes the crowd |

## AI use

### In development

- **Tools:** Claude Code with Claude Opus 5.5 (Anthropic) wrote nearly all of the code (Unity C#, the Python
  server), the Blender scripts behind the procedural models, the deployment setup and these docs; 80 of the 97
  commits in this folder are co-authored by it. ChatGPT (OpenAI) generated three venue textures (including the worn
  concrete floor) and the glasses' concept art, one image per pair as I described it; Claude then wrote the Blender
  script that models the glasses from that art. I tried having ChatGPT (6.1 Sol) build the models too: it was much
  better at concept art and much worse at 3D models, so each did the part it was good at. I picked the
  characters' voices by ear from Fish Audio's voice library on its website; the backing beats are free tracks from
  Pixabay, not AI-generated.
- **How much:** about 80% of my total effort for the two demos, October 15 to November 1, 2026: two long Claude Code
  sessions, about 450 prompts, nearly all of them in the second.
- **How I prompted:** I described features in plain language and iterated from playtests, sending Quest screenshots,
  phone photos of the screen and server logs. I asked Claude to check its own work before I tried it (Unity renders
  and play-mode tests, a rendered video of the DJ's intro, the backend test suite), and set standing rules: never
  commit keys, edit the scene in place instead of rebuilding it, ask before downloading anything.
- **What it got right quickly:** the DJ host (pre-rendered lines plus the player's name voiced live); the tug-of-war
  scoreboard and its tie zone; facial expressions from the faces' existing blendshapes; the gesture recognizer; the
  procedural mic, headphones, spray can and laser shows; the Cloud Run deployment.
- **What took many tries:** the spray can's grip and orientation in the hand (several rounds of headset screenshots);
  the DJ picking up and holding the mic; keeping each character's voice consistent (it changed every line until each
  got a fixed voice ID); a disconnect and robot-voice fallback on the cloud server (traced from its logs to the CPU
  being swamped by lip-sync jobs and a slow voice warm-up, fixed by moving lip sync onto the headset); shaders stripped from Quest builds (magenta spray mist);
  the MCs leaning too hard on robot insults.
- **Example prompts** (as typed):
  1. "I'd like a DJ now, one who also introduces both you and the MC. So, before battle: Greet crowd, introduce MC,
     crowd cheer, introduce player, crowd cheer, "let's begin" statement and then count in"
  2. "For the scoreboard, change the raw numbers to a tug of war flat bar. Then, there's a dead zone in the middle
     that counts as a tie."
  3. "Spray can is 180 degrees flipped when grabbing, and the particle effect is magenta debug color and desynced
     stereo from each eye."
  4. "The MCs drop a lot of disses about being a robot, that needs to scale back. It kind of breaks the immersion for
     the player."
  5. "I want a generic gesture controller that recognizes a gesture and fires an event, so it can be wired up to
     animator or script or whatever"

### In the game

| What | Model / service |
|---|---|
| Hearing you rap | Deepgram Nova-3, streaming (or Whisper via OpenRouter, or local faster-whisper) |
| Rating your bars and the crowd's and MC's reactions | Jev (via OpenRouter) |
| The MCs' comebacks, the interview summary | Google Gemini 3.1 Flash-Lite (via OpenRouter) |
| Every voice: MCs, DJ, interviewer | Fish Audio Drama 3 (or ElevenLabs v3; locally Qwen3-TTS, Breeze TTS 2 or IndexTTS-2.5). The interviewer's and DJ's lines are pre-rendered with it; the player's name is voiced live. |
| The MCs' lip sync | Meta's Oculus Lipsync, on the headset, from the voice's audio (Rhubarb Lip Sync on the server where that isn't available) |

## Attributions

- **Characters and animation:** Microsoft Rocketbox avatar library, MIT License.
- **Music:** backing beats from Pixabay (Pixabay Content License), listed in
  [backend/songs/CREDITS.md](backend/songs/CREDITS.md).
- **Crowd sounds:** "Free Crowd Cheering Sounds" by Gregor Quendel (www.gregorquendel.com), CC BY 4.0; trimmed and
  mixed for the game.
- **Spray can rattle:** clacks cut from "Shake Spray Can" by freesound_community, Pixabay (Pixabay Content License).
- **Lip sync:** Oculus Lipsync 29.0.0, Meta Platforms, Oculus SDK License.
- **Hats:** "Hat Hip- Hop" by T-Art, "Cowboy Hat1" by denj666, "Lederhut" by Poleegon, "Tophat" by Mike Rowley,
  "US Military Cap" by Charlie Tinley, all CC BY 4.0 on Sketchfab ([unity/credits.txt](unity/credits.txt)).
- **Colour names:** the xkcd colour survey, Randall Munroe, CC0.
- **Font:** Liberation Sans, SIL Open Font License 1.1.
- **AI-generated:** three venue textures, including the floor, and the app icon (ChatGPT); the voices (above); the
  posters are in-engine character screenshots laid out by script.
- **Made for the project:** the robot, mic, headphones, glasses, clown wig, spray can, crowd, laser shows and smoke,
  generated
  by the scripts in `art/`.

Full details, sources and the assets whose origin is still unconfirmed: [CREDITS.md](CREDITS.md).
