# SWISH user guide

Everything the app does, in detail. Setup and building are in the [README](../README.md); the downloadable app has a shorter [QUICKSTART](QUICKSTART.md).

## The app

**First run** goes Home → *How to calibrate* (a looping demo clip) → a 3-2-1 countdown → calibration:
a ball snakes across the screen row by row for ~30 s and you follow it with your palm (the X's turn pink
as it passes them). Say "start" or click to begin, "cancel" or Esc to back out. The mapping is fitted
from hundreds of samples, corrected for how far your hand trails the ball, and a bad run (hand lost too
often, path not followed) is refused with a reason. It's saved to `%LOCALAPPDATA%\SWISH\calibration.json`,
so later launches go straight to Controls; **Settings › Re-calibrate** redoes it.

**Controls** shows, for the selected game, what each hand does and the voice commands (the essential ones
up front, the rest by category). The sidebar has:

- **My games**: Desktop, Minecraft, Aimlabs, Rocket League, Bloons TD 6. Picking one switches the voice
  preset. **+ More** is a placeholder for adding your own.
- **Live status**: hands in view, voice state, mic level and the last thing heard.
- **Pause** turns the camera *and* the microphone off: nothing is recorded, transcribed or sent until you
  resume. The small **✋** button next to it turns off only hand tracking (voice keeps working).
- **Settings**: camera, microphone, GPU (run hand tracking on any DirectX 12 GPU via DirectML, or the CPU),
  voice assistant (the spoken replies: voice, volume, mute, test), and Advanced › debug dashboard.

### Hand controls

| Hand | Gesture | Does |
|---|---|---|
| Right | move your palm | moves the cursor |
| Right | thumb + index pinch | left click (hold to drag) |
| Right | thumb + middle pinch | right click |
| Right | thumb + ring pinch, then move | **scroll**: the cursor stays put; hand up/down scrolls up/down, left/right scrolls sideways |
| Right | fist | "lift the mouse" to reposition your hand |
| Left | fold a finger | holds a key: middle W, ring A, index D, thumb Space, pinky S |

Each game labels these on its Controls page (e.g. in Minecraft the scroll pinch changes hotbar slot).

**Ctrl+Alt+M** hand mouse on/off · **Ctrl+Alt+J** mouse mode · **Ctrl+Alt+V** show/hide window · **Ctrl+Alt+Q** quit.

### How it fits together

```
SWISH.App (WPF, tray)
├─ GestureEngine  (HandGestureRecognition)  camera → palm/landmark models → HandMouse → mouse   [own thread]
└─ VoiceEngine    (VoiceKeys)               mic → ElevenLabs STT → presets → keystrokes          [async]
```

Each engine is a class the console programs also use (`src/HandGestureRecognition/Program.cs` and
`src/VoiceKeys/Program.cs` are thin wrappers), so they can still be run and debugged separately
(`.\scripts\run.ps1 -Console`). If one engine can't start (no camera, no API key), the other keeps working.
The console gesture app keeps its own calibrations: `c` (hold still on 9 targets) and `p` (follow the dot).

## Custom gestures & commands

**CUSTOMIZE +** on Controls (or any **+ Add** card) opens the command editor, in three steps:

1. **How do you trigger it?** *Say a phrase*, *make a gesture*, or both. Phrases are added as chips, and you
   can say one out loud to check it's heard (voice commands are paused on this page, so nothing gets pressed).
   A gesture is recorded with a guided ~20 s routine (tilt it, move it around) so it's recognised from
   different angles, for the left, right or either hand; it can run once or hold its keys while held.
2. **What should it do?** A list of steps: *Press* a key (click the box, then press the key, chord or
   mouse button), *Hold* one for some milliseconds, *Wait*, or *Type* text. Drag the dotted grip to reorder
   (or focus it and press Up/Down). *Edit as text* shows the step language for power users
   (`ctrl+c`, `hold w 500`, `wait 100`, `latch shift`, `type Hello`).
3. **Name it**, and optionally pick a category so it appears in that group on Controls.

A summary says in plain words what the command will do, and the camera on the right shows whether your
saved gestures are recognised. Recognition compares your hand with the recorded samples (no retraining):
positions are relative to the wrist and palm size, and finger extension is weighted so similar poses stay
apart. While a custom gesture is active, that hand's mouse and finger keys pause.
Saved to `%LOCALAPPDATA%\SWISH\custom-functions.json`.

## Voice

Listening is continuous: say a command and it fires once you pause (~0.3 s). Push-to-talk is still
available (`pushToTalk.enabled` in `src/VoiceKeys/settings.json`).

Voice can also control the hand tracking, in every preset (it presses the same hotkeys you would):

| Say | Does |
|---|---|
| "toggle hand mouse" / "gestures" | turn hand-mouse on/off (Ctrl+Alt+M) |
| "mouse mode" | Relative → Absolute → Joystick (Ctrl+Alt+J) |
| "show camera" / "hide camera" | show/hide the SWISH window (Ctrl+Alt+V) |

Switch games by voice ("minecraft mode", "desktop mode"…) or in the sidebar.

### Desktop

Everyday Windows: "enter", "escape", "switch window", "show desktop", "screenshot", "new tab",
"go back", "page down", "volume up", "play"… plus the scroll pinch. See
[`VoiceKeys/presets/desktop.json`](../src/VoiceKeys/presets/desktop.json).

### Minecraft

Your hand aims the camera (hand-mouse in Relative mode), voice does everything else: "walk", "run",
"stop", "a little forward", "sneak", "mine", "really long mine", "keep mining", "place", "eat",
"shoot", "slot three", "inventory", "drop"… See
[`VoiceKeys/presets/minecraft.json`](../src/VoiceKeys/presets/minecraft.json).

### Rocket League

Your hand keeps the mouse, voice drives the car: "drive", "stop", "reverse", "left", "hard left",
"left a little", "boost", "little boost", "jump", "big jump", "flip left", "backflip"… Modifiers
("hard", "little", "really") scale how long a key is held. See
[`VoiceKeys/presets/rocket-league.json`](../src/VoiceKeys/presets/rocket-league.json).

**Heads-up:** a pinch is a left click, and boost/mine are also the left mouse button, so a pinch
while voice-boosting ends the boost early (and vice versa). Turn hand-mouse off ("toggle hand
mouse") or rebind if that gets in the way.

### Aimlabs and Bloons TD 6

Aimlabs: "shoot", "hold fire", "scope", "reload", "jump", "crouch"… (keybinds are the defaults we could find;
check [`aimlabs.json`](../src/VoiceKeys/presets/aimlabs.json) against your settings). Bloons TD 6: say a tower's name
to pick it ("dart monkey", "ninja monkey"…), "upgrade one/two/three", "sell", "target", "start", "pause"… See
[`bloons-td-6.json`](../src/VoiceKeys/presets/bloons-td-6.json).

## VoiceKeys config

- `src/VoiceKeys/settings.json`: push-to-talk, latency, spoken replies (voice, volume), starting preset.
  The app's own choices (camera, mic, GPU, voice assistant, last game) are kept in
  `%LOCALAPPDATA%\SWISH\app-settings.json`.
- `src/VoiceKeys/presets/*.json`: one file per game. `game` makes it a card in *My games*, `handLabels` names
  the hand controls, and each command can have a `label`, `category` and `essential` flag for the Controls
  page. `gestures.json` is shared via `"include"`. Edit them in the source folder; building copies them next
  to the app (tray → *Open presets folder* shows the copy in use).
- `dotnet run --project src/VoiceKeys -- --try "hard left"` shows what a phrase would do (no mic, no keys).
- `dotnet run --project src/VoiceKeys -- --selftest "drive"` runs the phrase through ElevenLabs TTS → STT.
