# hand-gesture-recognition-using-onnx — C# port

C# / .NET 8 port of the Python inference app (`app.py`, `model/*`, `utils/*`), using
**OpenCvSharp4** in place of `cv2` and **Microsoft.ML.OnnxRuntime** in place of `onnxruntime`.
It loads the exact same `.onnx` files and label/CSV files as the Python version.

## Setup

1. Copy this `HandGestureRecognition/` folder into the root of the original repo
   (next to `app.py` and `model/`). The app uses the same relative paths as the Python
   code (`model/...`), so it must run with the repo root as the working directory.
2. From the repo root:

```
dotnet run --project HandGestureRecognition -c Release
dotnet run --project HandGestureRecognition -c Release -- --device 1 --width 1280 --height 720
dotnet run --project HandGestureRecognition -c Release -- --image hand.png
```

Options are identical to the Python version: `-d/--device`, `-im/--image`, `-wi/--width`,
`-he/--height`, `-mdc/--min_detection_confidence`, `-dif/--disable_image_flip`.
Added here: `-sw/--show_window`, `-cal/--calibrate`, `-calp/--calibrate_pursuit`, and `-rec/--record`
(saves the annotated feed to `output.mp4`; off by default, it writes ~9 MB a minute).

With no hand in view for 1.5 s, the models run ~5 times a second instead of every frame
(`GestureOptions.IdleAfterSeconds` / `IdleFrameInterval`), which cuts idle CPU by about two thirds.

Keys are also identical: `ESC` quit, `n` normal, `k` log keypoints, `h` log point history,
`0`–`9` class ID, `a` toggle auto-repeat of the last number.

### Platform notes
- **Windows**: works as-is (`OpenCvSharp4.runtime.win` is referenced conditionally).
- **Linux/macOS**: add the OpenCvSharp4 native runtime package for your platform
  (or build OpenCvSharpExtern yourself) — OpenCvSharp ships the managed wrapper and the
  native binaries separately.
- **GPU**: replace `Microsoft.ML.OnnxRuntime` with `Microsoft.ML.OnnxRuntime.Gpu`.
  The code already tries the CUDA provider first and silently falls back to CPU,
  mirroring the Python provider list.

## File mapping

| Python                                                       | C#                                   |
|--------------------------------------------------------------|--------------------------------------|
| `app.py`                                                     | `Program.cs`                         |
| `model/palm_detection/palm_detection.py`                     | `Model/PalmDetection.cs`             |
| `model/hand_landmark/hand_landmark.py`                       | `Model/HandLandmark.cs`              |
| `model/keypoint_classifier/keypoint_classifier.py`           | `Model/KeyPointClassifier.cs`        |
| `model/point_history_classifier/point_history_classifier.py` | `Model/PointHistoryClassifier.cs`    |
| (session/provider setup repeated in each Python class)       | `Model/OnnxSessionFactory.cs`        |
| `utils/utils.py`                                             | `Utils/ImageUtils.cs`                |
| `utils/cvfpscalc.py`                                         | `Utils/CvFpsCalc.cs`                 |
| `dict` (ordered) / `deque(maxlen=N)`                         | `Utils/OrderedMap.cs` / `Utils/BoundedQueue.cs` |

**Not ported** (training tooling, stays in Python): the two `.ipynb` training notebooks,
`make_argmax.py`, and `tflite_to_onnx.sh`. The C# app writes `keypoint.csv` /
`point_history.csv` in the same format, so you can collect data in C# and train in Python.

## Behaviour notes

The port is deliberately faithful, including a few quirks of the original that you may
want to fix later:

- **Tracking IDs**: when a palm matches an existing one, the original uses the *list index*
  of the nearest stored center + 1 as the trackid, not that center's actual trackid.
  Kept as-is.
- **CSV logging**: `logging_csv` receives the trackid/landmarks of the *last* hand in the
  frame only, so with several hands only one is logged in keypoint mode. Kept as-is.
- **Zip semantics**: Python `zip()` silently truncates to the shortest list; the C# loops
  use the minimum count in the same places.

Small, intentional differences:

- Python's `HandLandmark` resizes and rotates an image only to read its width/height.
  The C# version computes those sizes arithmetically (verified identical to OpenCV's
  `resize`/`warpAffine` output sizes), which saves two image allocations per hand per frame.
- Out-of-range crop slices are clamped (NumPy-style) and never produce a 0×0 image, which
  in Python would raise inside `cv2.resize`.
- If the capture backend reports 0 FPS, the video writer uses 30 FPS instead of failing.
