**SWISH - Stormhacks2026 Submission**

Serial Wireless Interactive System (for) Humans: you become the game controller. Your hand drives
the mouse through the webcam, and your voice presses the keys.

| Project | What it does |
|---|---|
| [`SWISH.App/`](SWISH.App/) | **The app.** One window + tray icon running both engines below. |
| [`HandGestureRecognition/`](HandGestureRecognition/README.md) | Webcam hand tracking → mouse. Palm moves the cursor, pinch clicks, fist "lifts" the mouse. Still runs on its own as a console app. |
| [`VoiceKeys/`](VoiceKeys/) | Voice → keystrokes, using ElevenLabs realtime speech-to-text and text-to-speech. Switchable presets (Desktop, Rocket League, Minecraft). Still runs on its own as a console app. |

## Running it

```powershell
setx ELEVENLABS_API_KEY "your-key"     # once
.\run.ps1
```

Or open `SWISH.slnx` in Visual Studio, set **SWISH.App** as the startup project and press F5.

It opens with an animated title card (~4 s; click or press a key to skip, or start with `--no-splash`).
`SWISH.exe --render-splash <folder>` writes its frames to PNGs without starting the camera or mic.

The window shows the camera with hand tracking on the left, and voice on the right: connection,
the active preset, a live transcript while you hold **Caps Lock**, and a log of everything heard
and the keys it pressed. Closing the window keeps SWISH running in the tray (double-click to bring
it back; right-click for hand mouse, voice, preset, recalibrate, exit).

First run opens a full-screen calibration: hold your hand still on each target. It's saved to
`%LOCALAPPDATA%\SWISH\calibration.json`; **Recalibrate…** redoes it.

**Follow the dot…** is the alternative: a dot snakes across the screen row by row for ~30 s and you
follow it with your palm along the highlighted line. The camera view fills the screen behind it, and
the border turns green while your hand is tracked (red when it isn't). No holding still (easier with an unsteady hand), and the mapping is fitted from
hundreds of samples across the whole screen. It measures how far your hand trails the dot and
corrects for it, drops frames where tracking glitched, and refuses to save a bad run (hand lost too
often, hand barely moved, path not followed) with a message saying why. In the console app, press
`p` (or start with `--calibrate_pursuit`); `c` is still the 9-point one.

**Ctrl+Alt+M** hand mouse on/off · **Ctrl+Alt+J** mouse mode · **Ctrl+Alt+V** show/hide window · **Ctrl+Alt+Q** quit.

### How it fits together

```
SWISH.App (WPF, tray)
├─ GestureEngine  (HandGestureRecognition)  camera → palm/landmark models → HandMouse → mouse   [own thread]
└─ VoiceEngine    (VoiceKeys)               mic → ElevenLabs STT → presets → keystrokes          [async]
```

Each engine is a class the console programs also use (`HandGestureRecognition/Program.cs` and
`VoiceKeys/Program.cs` are thin wrappers), so they can still be run and debugged separately
(`.\run.ps1 -Console`). If one engine can't start (no camera, no API key), the other keeps working.

## Using them together

Voice can control the hand tracking, in every preset (it presses the same hotkeys you would):

| Say | Does |
|---|---|
| "toggle hand mouse" / "gestures" | turn hand-mouse on/off (Ctrl+Alt+M) |
| "mouse mode" | Relative → Absolute → Joystick (Ctrl+Alt+J) |
| "show camera" / "hide camera" | show/hide the SWISH window (Ctrl+Alt+V) |

Switch command sets by voice: "rocket league mode", "minecraft mode", "desktop mode", or with the
preset picker in the window.

### Minecraft

Your hand aims the camera (hand-mouse in Relative mode), voice does everything else: "walk", "run",
"stop", "a little forward", "sneak", "mine", "really long mine", "keep mining", "place", "eat",
"shoot", "slot three", "inventory", "drop"… See
[`VoiceKeys/presets/minecraft.json`](VoiceKeys/presets/minecraft.json).

### Rocket League

Your hand keeps the mouse, voice drives the car: "drive", "stop", "reverse", "left", "hard left",
"left a little", "boost", "little boost", "jump", "big jump", "flip left", "backflip"… Modifiers
("hard", "little", "really") scale how long a key is held. See
[`VoiceKeys/presets/rocket-league.json`](VoiceKeys/presets/rocket-league.json).

**Heads-up:** a pinch is a left click, and boost/mine are also the left mouse button, so a pinch
while voice-boosting ends the boost early (and vice versa). Turn hand-mouse off ("toggle hand
mouse") or rebind if that gets in the way.

## VoiceKeys config

- `VoiceKeys/settings.json`: push-to-talk key, voice, latency settings, starting preset.
- `VoiceKeys/presets/*.json`: one file per command set. `gestures.json` is shared via `"include"`.
  Edit them in the source folder; building copies them next to the app (tray → *Open presets folder* shows the copy in use).
- `dotnet run --project VoiceKeys -- --try "hard left"` shows what a phrase would do (no mic, no keys).
- `dotnet run --project VoiceKeys -- --selftest "drive"` runs the phrase through ElevenLabs TTS → STT.
