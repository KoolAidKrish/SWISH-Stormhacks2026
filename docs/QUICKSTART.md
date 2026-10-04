# SWISH quickstart

**S**erial **W**ireless **I**nteractive **S**ystem (for) **H**umans: your right hand is the mouse, your left hand is
the keyboard, your voice does the rest. This folder is the ready-to-run app: one `SWISH.exe`, nothing to install.

## You need

- Windows 10 or 11 (64-bit)
- A webcam (for hand control) and a microphone (for voice)
- An [ElevenLabs](https://elevenlabs.io) API key for voice commands. Without one, hand control still works.

## Run it

1. Give SWISH your ElevenLabs key, once, in PowerShell or Command Prompt:

   ```
   setx ELEVENLABS_API_KEY "your-key"
   ```

2. Double-click `SWISH.exe`. The first launch takes a few seconds longer while it unpacks itself.
   If Windows SmartScreen says it "protected your PC", choose **More info → Run anyway** (the exe isn't code-signed).
3. Follow the on-screen calibration: a ball moves across the screen for about 30 seconds and you follow it with your palm.
4. You land on **Controls**. Pick a game on the left (Desktop, Minecraft, Aimlabs, Rocket League, Bloons TD 6)
   and press **▶ TUTORIAL** to see the hand controls in action.

Closing the window keeps SWISH running in the system tray: double-click the tray icon to bring it back,
right-click it for options or **Exit**.

## Hand controls

| Hand | Gesture | Does |
|---|---|---|
| Right | move your open hand | moves the cursor |
| Right | make a fist | freezes the cursor so you can reposition your hand |
| Right | pinch thumb + index | left click (hold to drag) |
| Right | pinch thumb + middle | right click |
| Right | pinch thumb + ring, then move | scroll |
| Left | fold a finger | holds a key: middle W, ring A, index D, thumb Space, pinky S |

Click a gesture card on the Controls page to rebind what it presses for that game.

**Ctrl+Alt+M** hand mouse on/off · **Ctrl+Alt+J** mouse mode · **Ctrl+Alt+V** show/hide window · **Ctrl+Alt+Q** quit.

## Voice

Just talk: a command fires when you pause. Each game has its own commands (the Controls page lists them), for
example "enter", "new tab" and "volume up" on Desktop, or "walk", "mine" and "inventory" in Minecraft.
Switch games by saying "desktop mode", "minecraft mode", and so on.

**Pause** in the sidebar turns the camera and the microphone off completely until you resume.

## Your data

Calibration, settings and your own commands are saved in `%LOCALAPPDATA%\SWISH`. To remove SWISH, exit it from
the tray, delete `SWISH.exe` and that folder.

More detail: the [user guide](https://github.com/KoolAidKrish/SWISH-Stormhacks2026/blob/main/docs/USER-GUIDE.md).
