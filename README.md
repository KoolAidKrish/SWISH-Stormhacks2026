**SWISH - Stormhacks2026 Submission**

Serial Wireless Interactive System (for) Humans: you become the game controller. Your hand drives
the mouse through the webcam, and your voice presses the keys.

## Download

Grab **`SWISH-win-x64.zip`** from the [latest release](https://github.com/KoolAidKrish/SWISH-Stormhacks2026/releases/latest),
unzip it and run `SWISH.exe`. It's a single self-contained exe (Windows 10/11, 64-bit, no .NET install needed).
The [quickstart](docs/QUICKSTART.md) in the zip covers the API key, calibration and controls.

## Repository layout

```
SWISH-Stormhacks2026/
├─ README.md              you are here
├─ SWISH.slnx             the Visual Studio solution (the three projects in src/)
├─ src/
│  ├─ SWISH.App/          the app: WPF window + tray icon running both engines below
│  ├─ HandGestureRecognition/   webcam hand tracking → mouse and keys (also runs alone as a console app)
│  └─ VoiceKeys/          voice → keystrokes via ElevenLabs, one preset per game (also runs alone)
├─ docs/
│  ├─ QUICKSTART.md       for people running the exe (ships inside the release zip)
│  └─ USER-GUIDE.md       everything the app does, in detail
├─ scripts/
│  ├─ run.ps1             build from source and start the app
│  └─ publish.ps1         build the single-file exe + zip into release/
├─ landing/               the project website (Vite + React, deployed by Netlify; see netlify.toml)
└─ release/               output of scripts/publish.ps1 (git-ignored)
```

| Project | What it does |
|---|---|
| [`src/SWISH.App/`](src/SWISH.App/) | **The app.** One styled window + tray icon running both engines below. |
| [`src/HandGestureRecognition/`](src/HandGestureRecognition/README.md) | Webcam hand tracking → mouse and keys. Palm moves the cursor, pinches click and scroll, fist "lifts" the mouse. Still runs on its own as a console app. |
| [`src/VoiceKeys/`](src/VoiceKeys/) | Voice → keystrokes, using ElevenLabs realtime speech-to-text and text-to-speech. One preset per game (Desktop, Minecraft, Aimlabs, Rocket League, Bloons TD 6). Still runs on its own as a console app. |

```
SWISH.App (WPF, tray)
├─ GestureEngine  (HandGestureRecognition)  camera → palm/landmark models → HandMouse → mouse   [own thread]
└─ VoiceEngine    (VoiceKeys)               mic → ElevenLabs STT → presets → keystrokes          [async]
```

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
setx ELEVENLABS_API_KEY "your-key"     # once
.\scripts\run.ps1                      # build and start the app
.\scripts\run.ps1 -Console             # or the two console programs side by side
```

Or open `SWISH.slnx` in Visual Studio, set **SWISH.App** as the startup project and press F5.

It opens with an animated title card (~4 s; click or press a key to skip, or start with `--no-splash`).
`SWISH.exe --render-splash <folder>` writes its frames to PNGs without starting the camera or mic.

### Making a release

```powershell
.\scripts\publish.ps1
```

This writes `release\SWISH\` (`SWISH.exe` + the quickstart as `README.md`) and `release\SWISH-win-x64.zip`.
Upload the zip to a GitHub Release rather than committing it: the exe is ~135 MB, over GitHub's 100 MB file limit.
Visual Studio's **Publish** (profile `FolderProfile`) produces the same exe in `release\SWISH\`.

## Learn more

- [User guide](docs/USER-GUIDE.md): first run and calibration, the Controls page, hand controls, custom gestures
  and commands, every game's voice commands, and the VoiceKeys config files.
- [Landing site](landing/README.md)
