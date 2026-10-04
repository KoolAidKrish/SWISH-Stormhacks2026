using HandGestureRecognition.Utils;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HandGestureRecognition.Model;

/// <summary>
/// Width/height of the rotated crop's bounding canvas, plus handedness
/// (0 = left hand, 1 = right hand, as reported by the model).
/// </summary>
public readonly record struct RotatedImageSizeLeftRight(int RotatedImageWidth, int RotatedImageHeight, float LeftHand0OrRightHand1);

/// <summary>Port of model/hand_landmark/hand_landmark.py.</summary>
public sealed class HandLandmark : IDisposable
{
    private readonly InferenceSession _session;
    /// <summary>True if the model runs on the GPU (DirectML); false if on the CPU (asked for, or fallback).</summary>
    public bool OnGpu { get; }
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private readonly int _inputH;
    private readonly int _inputW;
    private readonly float _classScoreTh;

    public HandLandmark(
        string modelPath = "model/hand_landmark/hand_landmark_sparse_Nx3x224x224.onnx",
        float classScoreTh = 0.50f,
        int? gpuAdapter = null)
    {
        _classScoreTh = classScoreTh;
        _session = OnnxSessionFactory.Create(modelPath, gpuAdapter, out bool onGpu);
        OnGpu = onGpu;
        _inputName = _session.InputNames[0];
        var dims = _session.InputMetadata[_inputName].Dimensions; // [N, 3, 224, 224]
        _inputH = dims[2];
        _inputW = dims[3];
        _outputNames = _session.OutputNames.ToArray(); // xyz_x21, hand_score, lefthand_0_or_righthand_1
    }

    /// <summary>
    /// Runs batched landmark inference on de-rotated palm crops.
    /// Returns 21 (x, y) landmarks per kept hand in full-frame pixel coordinates.
    /// </summary>
    public (List<Point[]> HandLandmarks, List<RotatedImageSizeLeftRight> RotatedImageSizeLeftRights) Run(
        IReadOnlyList<Mat> images,
        IReadOnlyList<RotRect> rects)
    {
        var landmarks = new List<Point[]>();
        var sizes = new List<RotatedImageSizeLeftRight>();
        int n = images.Count;
        if (n == 0)
        {
            return (landmarks, sizes);
        }

        // ---- PreProcess -------------------------------------------------------
        int chw = 3 * _inputH * _inputW;
        var input = new float[n * chw];
        var resizedSizes = new Size[n];
        var resizeScales = new (float W, float H)[n];
        var halfPadSizes = new (int W, int H)[n];

        for (int i = 0; i < n; i++)
        {
            var image = images[i];
            var (padded, resized) = ImageUtils.KeepAspectResizeAndPad(image, _inputW, _inputH);
            using (padded)
            using (resized)
            {
                resizeScales[i] = (
                    (float)(resized.Cols / (double)image.Cols),
                    (float)(resized.Rows / (double)image.Rows));

                int padH = padded.Rows - resized.Rows;
                int padW = padded.Cols - resized.Cols;
                halfPadSizes[i] = (Math.Max(0, ImageUtils.FloorDiv(padW, 2)), Math.Max(0, ImageUtils.FloorDiv(padH, 2)));
                resizedSizes[i] = new Size(resized.Cols, resized.Rows);

                ImageUtils.BgrToRgbChwNormalized(padded, input, i * chw);
            }
        }

        // ---- Inference --------------------------------------------------------
        float[] xyzX21s, handScores, leftRights;
        using (var runOptions = new RunOptions())
        using (var inputValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { n, 3, _inputH, _inputW }))
        using (var outputs = _session.Run(runOptions, new[] { _inputName }, new[] { inputValue }, _outputNames))
        {
            var outs = outputs.ToArray();
            xyzX21s = outs[0].GetTensorDataAsSpan<float>().ToArray();     // [N, 63]
            handScores = outs[1].GetTensorDataAsSpan<float>().ToArray();  // [N, 1]
            leftRights = outs[2].GetTensorDataAsSpan<float>().ToArray();  // [N, 1]
        }

        // ---- PostProcess ------------------------------------------------------
        for (int i = 0; i < n; i++)
        {
            if (!(handScores[i] > _classScoreTh) || i >= rects.Count)
            {
                continue;
            }

            var rect = rects[i];
            var (scaleW, scaleH) = resizeScales[i];
            var (halfPadW, halfPadH) = halfPadSizes[i];

            // Python resizes the 224-scaled crop back up with cv2.resize(fx=1/scale) and only
            // uses its shape. OpenCV computes that size as cvRound(size * fx), so do the same
            // arithmetic without allocating an image.
            float invScaleW = 1f / scaleW;
            float invScaleH = 1f / scaleH;
            int viewW = (int)Math.Round(resizedSizes[i].Width * (double)invScaleW, MidpointRounding.ToEven);
            int viewH = (int)Math.Round(resizedSizes[i].Height * (double)invScaleH, MidpointRounding.ToEven);

            // Landmarks back to crop pixel coordinates.
            var rescaled = new (int X, int Y)[21];
            for (int k = 0; k < 21; k++)
            {
                float x = xyzX21s[i * 63 + k * 3] / _inputH;
                float y = xyzX21s[i * 63 + k * 3 + 1] / _inputH;
                rescaled[k] = (
                    (int)((x * _inputW - halfPadW) / scaleW),
                    (int)((y * _inputH - halfPadH) / scaleH));
            }

            // Rotation matrix that undoes the de-rotation (rotate "without crop").
            var imageCenter = new Point2f(viewW / 2, viewH / 2);
            double m00, m01, m02, m10, m11, m12;
            using (var rotationMatrix = Cv2.GetRotationMatrix2D(imageCenter, -(int)rect.Angle, 1.0))
            {
                m00 = rotationMatrix.Get<double>(0, 0);
                m01 = rotationMatrix.Get<double>(0, 1);
                m02 = rotationMatrix.Get<double>(0, 2);
                m10 = rotationMatrix.Get<double>(1, 0);
                m11 = rotationMatrix.Get<double>(1, 1);
                m12 = rotationMatrix.Get<double>(1, 2);
            }
            double absCos = Math.Abs(m00);
            double absSin = Math.Abs(m01);
            int boundW = (int)(viewH * absSin + viewW * absCos);
            int boundH = (int)(viewH * absCos + viewW * absSin);
            m02 += boundW / 2.0 - imageCenter.X;
            m12 += boundH / 2.0 - imageCenter.Y;

            // The rotated image is (boundW x boundH); Python only uses its shape, so skip warpAffine.
            int rotatedImageWidth = boundW;
            int rotatedImageHeight = boundH;
            int rotatedHandHalfWidth = rotatedImageWidth / 2;
            int rotatedHandHalfHeight = rotatedImageHeight / 2;

            var handLandmarks = new Point[21];
            for (int k = 0; k < 21; k++)
            {
                var (x, y) = rescaled[k];
                int xLs = (int)(m00 * x + m01 * y + m02);
                int yLs = (int)(m10 * x + m11 * y + m12);
                handLandmarks[k] = new Point(
                    (int)(xLs + rect.Cx - rotatedHandHalfWidth),
                    (int)(yLs + rect.Cy - rotatedHandHalfHeight));
            }

            landmarks.Add(handLandmarks);
            sizes.Add(new RotatedImageSizeLeftRight(rotatedImageWidth, rotatedImageHeight, leftRights[i]));
        }

        return (landmarks, sizes);
    }

    public void Dispose() => _session.Dispose();
}
