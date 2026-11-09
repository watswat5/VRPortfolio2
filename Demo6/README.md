# Quest Wi-Fi Heatmap

> **⚠️ Use a personal or unmanaged Quest 3. School-managed headsets won't work.** Headsets enrolled in device
> management (ArborXR or Meta Quest for Business, as the class headsets are) block sideloaded apps from reading
> the room scan and saving anchors: Horizon OS rejects every request with `ERROR_PACKAGE_UNTRUSTED`. The app then
> can't load your room (**Preview > Reload room** reports `Failure`; at launch it silently falls back to one of
> Meta's sample rooms) or save manual anchors (`FailurePermissionInsufficient`), so readings aren't placed in your
> real room. Permissions, the network and restarting don't change it. Either run it on a Quest that isn't enrolled
> in management, or have the headset's administrator install `com.wifiheatmap.prototype` through ArborXR or allow
> it spatial data. To check a headset: `adb shell dumpsys device_policy | grep -A2 "Device Owner"` shows the
> management app if there is one.

A mixed-reality Wi-Fi survey tool for Meta Quest. A small ESP32 probe clipped to your left or right controller measures the
signal of one Wi-Fi access point as you walk around, and the headset paints it into your real room as a translucent
cloud of coloured voxels (weak to strong), so you can see where coverage is good and where it drops.

- **Pick any radio, or a whole network:** scan for access points and choose one by its BSSID and channel, or, where a
  network has several on one channel (campus Wi-Fi), all of them at once, each reading the strongest as a roaming
  device would connect; measuring needs no password and doesn't join the network.
- **Placed in your room:** each reading is timestamped on the probe and matched to where the controller was at that
  moment, through a calibrated controller-to-antenna offset, then anchored to the room.
- **See it in place:** a live voxel cloud in passthrough, hidden behind real walls, furniture, people and your hand
  (Quest depth sensing), with an LED meter and dBm readout on the probe; a miniature of the room you can move and
  scale.
- **Keep it:** record a survey and replay it later, anchored to the same room.

This is a prototype: see [docs/GUIDE.md](docs/GUIDE.md) for what's verified and what isn't.

## Video

https://youtu.be/F0tgIouySAU

## Release

- **Quest APK:** https://github.com/ugavrclass2026/portfolio-2-watswat5/releases/tag/Alpha

## Running it

You need a Quest 3 (or 3S), the WiFiProbe ESP32 probe with its firmware, and the printed clip that holds it on a
Touch Plus controller (left or right), or an M5StickC Plus2 running the same firmware, either strapped to your wrist (hand
tracking; see [the guide](unity/README.md#probe-on-the-wrist-hand-tracking)) or clipped to a controller on its own
printed mount (`hardware/m5_mount.py`). Building the probe and flashing the firmware: [docs/GUIDE.md](docs/GUIDE.md#quick-start)
and [firmware/](firmware/README.md); the clip: [hardware/](hardware/README.md).

1. Install the APK on the Quest in developer mode: `adb install -r demo6.apk`. Allow the Bluetooth, scene and
   spatial permissions it asks for. Have your room set up in Quest Space Setup (the app can start it).
2. Power the probe and clip it onto a controller (the XIAO pod, or the M5StickC Plus2 on its M5 mount), or strap the
   M5StickC Plus2 on top of your forearm (long side toward your fingers) and put the controllers down. In **Mount**, set **Side** (left/right) and **On** (controller or
   wrist) to match.
   In the menu's **Probe** tab, **Connect**; Android asks for
   the probe's pairing PIN (the prototype default is `123456`, set in the firmware; it is not a Wi-Fi password).
3. **Network:** **Scan**, then tap a row to measure it: an access point, or a network's access points on one channel
   ("N APs", read as the strongest).
4. **Mount:** calibrate the first time (**Align probe**, centre the real antenna in the cyan ball and line the probe up
   with the arrows, press the
   probe controller's trigger, or on the wrist pinch your other hand's index finger or press the probe's **A**;
   **Save offset**). Each controller and wrist keeps its own saved offset. **Align meter** places the EMF meter on the
   probe the same way.
5. Walk the room with the probe: the cloud grows as you go. **Cloud** sets the voxel size, opacity and colours;
   **Sessions > Record** keeps a survey to replay later. For surveys you'll replay, record against a manual anchor
   (**Anchors > Place anchor at probe**, **Use**): editing the room in Space Setup can rotate sessions recorded
   against the room.

## Controls

The probe's controller has the same layout on either side:

| Control | Does |
|---|---|
| X (probe on left) or A (probe on right), or the hand menu gesture | Show or hide the menu |
| Trigger with a controller ray, pinch with a hand ray, or poke with a fingertip | Use the menu |
| Probe controller's index trigger | Calibration: confirm the probe's fit in the frozen target |
| Y (probe on left) or B (probe on right) | Calibration: cancel |

With the probe on your wrist (controllers put down):

| Control | Does |
|---|---|
| Hand menu gesture | Show or hide the menu |
| Pinch with a hand ray, or poke with a fingertip | Use the menu |
| Other hand's index pinch, or the probe's **A** button | Calibration: confirm the probe's fit in the frozen target |
| Other hand's middle-finger pinch, or the probe's **B** button | Calibration: cancel |
| Pinch near the room miniature (one hand, or both to scale) | Move, rotate or scale it |

## AI use

- **Tools:** two, one after the other.
  - **ChatGPT (OpenAI) started it.** In a ChatGPT chat I worked out the architecture and the WiFiProbe v1 protocol
    (scan for access points, pick one, report whether the board supports 5 GHz), and ChatGPT Work wrote it up as
    [AGENTS.md](AGENTS.md), the architecture spec for coding agents (since updated as the design changed). Then, in
    Codex, ChatGPT wrote the ESP32 Arduino firmware (later adding BLE as a transport, with passkey pairing and a
    status LED on an assignable pin), the first Unity test component and its Android BLE bridge in Java
    (`Esp32BleSignalTester.cs`, `Esp32BleBridge.java`), the two concept images in `docs/concepts/` (a clamshell
    controller mount, since abandoned) and a first printable grip.
  - **Claude Code with Claude Opus 5.5 (Anthropic) took over** and wrote nearly everything after that: the probe
    mount, the Quest MR project setup, the EMF-meter test scene and config menu, clock sync between probe and headset,
    session recording and anchors (with tests), the voxel cloud, the room preview with its ray-marched fog, the visual
    tuning and the depth occlusion (custom shader code). It also reworked ChatGPT's test component into the app's
    probe client, and did all the work after that: the probe on either controller, the M5StickC Plus2 as a second
    probe (on the wrist with hand tracking, or on a controller in its own printed mount, with its screen, buzzer and
    buttons through protocol 1.1), larger BLE packets so a busy area's scan no longer stalls the link, and measuring
    a campus network's access points on a channel together (protocol 1.2).
- **Comparing them on the probe mount:** I had both ChatGPT and Claude generate grip-based mounts. Claude did much
  better. ChatGPT's first grip approximated the handle as an ellipse; I had to ask it to use the controller model as a
  boolean tool, then spell out the whole construction step by step (prompt 2 below) before it made a close-fitting
  shell, four revisions in all. Claude did that on its own, and also checked the fit by using booleans to measure the
  overlap volume. When I asked how other people keep a controller locked in place, it researched existing mounts and
  adapted one instead of designing from scratch. The final mount is Claude's modification of an existing gunstock
  adapter that snaps onto the controller's round face (credited below).
- **How much:** about a week and a half of work, late October to November 4, 2026: one ChatGPT architecture chat and
  one Codex task (about 35 prompts), then four Claude Code sessions to November 1 (roughly 85 prompts), and more
  Claude Code work from November 2 to 4 for the second probe and the large-scan fixes.
- **How I prompted:** I described what I wanted in plain language, often asking for options first ("Suggestions for
  occlusion?", "how hard?") and a code estimate before choosing one. I used both tools as a sounding board for design
  decisions I then made: moving the probe from a USB cable to the controller over BLE; one print material plus craft
  foam instead of a TPU liner; raw binary session files instead of a database; an anchor index in each record so
  multiple rooms work; three signal colours, with no colour meaning no sample. I gave them reference material to work
  from: the controller and board CAD files, product photos of the parts. I tested each change in the headset and sent
  back Quest screenshots. Claude checked its own work in an isolated copy of the Unity project (compiles, renders, APK
  builds) before I tried it, and labelled commits "untested on a headset" until I had.
- **What they got right quickly:** Claude: the probe mount. The hardware was the part I was most worried about: I've
  designed simpler printed parts before (project cases), but the Quest controller's organic shape meant I expected
  several slow test prints to get the fit right. Claude got it mostly right in a couple of attempts. ChatGPT: the
  firmware, which compiled cleanly for the XIAO ESP32-C5 in serial, BLE and dual modes and still built for ordinary
  ESP32 boards for testing; it also caught a Unity packaging problem (the Java bridge silently left out of the APK) by
  checking the built APK rather than trusting a successful build.
- **What took many tries:** the probe's placement on the controller needed a few corrections, and the rest was mostly
  the normal design loop of building, trying it in the headset and redesigning for what looked and felt right (the EMF
  meter's position, the cloud's look, the preview's fog). A few real problems along the way: a CAD fit check in
  Claude's session used up all my RAM (it switched to chunked checks and capped every later run); a headless Unity
  build hung behind a one-hour timeout until I called it out (it added a stall watchdog); I had to correct the screw
  pilot holes to 2.7-2.8 mm because my screws aren't self-tapping; and recorded sessions came back rotated after I
  edited the room in Space Setup (prompt 6 below), which is why replayable surveys now use a manual anchor.
- **In the app:** no AI models at runtime; the probe and headset do all the measuring and drawing.
- **Example prompts** (as typed):
  1. To ChatGPT, after it suggested a USB-cabled probe: "Or attach to controller, design a grip that holds the
     controller and an esp32+battery"
  2. To ChatGPT, on its fourth grip: "The model design should follow these steps: Choose a section of the controller
     that does not interfere with the tracking and grip button. Then, create a slice of that section. Scale the slice
     up by a couple mm, then use a boolean subtract with the originsal controller as the tool; this leaves an exact
     hole in the slice and a wall thickness of 1-2mm. Then, using this shell as a base, add clamping screw posts then
     slice in half vertically along grip direction to create the clamshell. On a clear part of this clamshell, create a
     project enclosure for the esp and battery and attach."
  3. To Claude: "In the hardware folder are reference files. I need the quest 3 mount modified to hold an esp32 and
     battery. It should be as small as possible since it will be attached to the top of the controller. Also, can't
     overhang the controller face where tracking LEDs are."
  4. "Suggestions for occlusion?" (then picking from the options it laid out, with a code estimate for each)
  5. "I want fog controls to be separate for full room and for preview. Preview fog density also needs to be able to
     go higher. Also, on the Preview, there is an arrow where the player's head is; this is cool, and there needs to be
     one also where the probe is."
  6. "All my recorded sessions are rotated now? (pull latest screenshot) But new recordings are locvalized correctly.
     Did the room anchor move or was it the recent changes?"

## Attributions

Project code and CAD-generated outputs are maintained in this repository. External assets are
attributed below where their source is recorded; a file's presence here does not grant a blanket
redistribution license.

| Asset | Attribution / source status |
|---|---|
| `docs/concepts/right-controller-wifi-probe-grip-concept.png`; `xiao-esp32-c5-right-controller-probe-concept.png` | AI-generated with ChatGPT (OpenAI), kept as design history. Prompted for a clamshell mount holding the ESP32 module (the one in the product reference images) and a LiPo battery, clamping onto the Quest controller. That concept, an earlier right-hand clamshell layout, was abandoned for the current ring-clip mount. |
| `docs/concepts/probe-grip-cad-r01` through `r04` previews; `hardware/out/*` | Project CAD previews and print exports generated from `hardware/probe_mount.py`. Their source geometry includes references listed below; generated outputs do not establish rights to those references. |
| The probe mount (`hardware/probe_mount.py`, `hardware/out/*`) | Derived from "Quest 3 Gunstock Adapters for Sanlaki Gunstock" on Printables ([printables.com/model/661157](https://www.printables.com/model/661157-quest-3-gunstock-adapters-for-sanlaki-gunstock)): its snap-on fit to the controller's round face was the starting point, modified for the probe. That model is Nebbish's remix (refitted for Quest 3 controllers) of "Magnetic Gunstock for Oculus Quest 1, Quest 2" by sanlaki (Thingiverse). Both are [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/); the mount, as a derivative, is shared non-commercially under the same terms. |
| `hardware/m5_lego.jpg` | M5Stack's mechanical drawing of the LEGO adapter plate from the [StickC-Plus2 Watch Kit](https://docs.m5stack.com/en/accessory/StickC-Plus2_Watch_Kit), used for the M5 mount's dimensions. Copyright M5Stack. |
| `hardware/Quest3.step`; `hardware/MetaQuestTouchPlus_Left.fbx`; `hardware/MetaQuestTouchPlus_Right.fbx` | Meta Quest 3 headset and Touch Plus controller models from Meta's developer website (Meta Platforms), used only as reference geometry to fit the probe mount. Subject to Meta's terms for those downloads. |
| `hardware/Seeed-Studio-XIAO-ESP32-C3.step` | Seeed Studio XIAO ESP32-C3 board model from the GrabCAD Community Library ([grabcad.com/library/seeed-studio-xiao-esp32-c3-1](https://grabcad.com/library/seeed-studio-xiao-esp32-c3-1)), used as the board envelope reference for the mount; subject to GrabCAD's terms of use. |
| `hardware/716JreSNasL._AC_SL1500_.jpg` | Product photo of the 1100 mAh LiPo battery, from its Amazon listing ([amazon.com/dp/B0GR14VMW5](https://www.amazon.com/dp/B0GR14VMW5)); kept as a dimension reference for the mount. Copyright the seller. |
| `hardware/61Tk6TDz8XL._SL1500_.jpg` | Product photo of the MTS-102 miniature toggle switch, from its Amazon listing ([amazon.com/dp/B0FH2B67R2](https://www.amazon.com/dp/B0FH2B67R2)); kept as a dimension reference for the mount. Copyright the seller. |
| `hardware/51c4xTsEAnL._AC_SL1400_.jpg` | Product photo of the Seeed Studio XIAO ESP32-C5 kit and antenna, from its Amazon listing ([amazon.com/dp/B0GWQ8K461](https://www.amazon.com/dp/B0GWQ8K461)); kept as a dimension reference for the mount. Copyright the seller. |
| `unity/Assets/TextMesh Pro/Fonts/LiberationSans.ttf` | Liberation Sans; the included `LiberationSans - OFL.txt` records Google/Red Hat notices and the SIL Open Font License 1.1. Keep that notice with the font. |

This project's `.gitignore` excludes Unity imports/builds, Python environments, editor recovery files,
local screenshots/recordings, signing material and Meta Dev Agent connection settings. Unity/Meta
packages resolve through the checked-in manifest and lockfile; retain their bundled notices. No
project-wide license is granted by this README.
