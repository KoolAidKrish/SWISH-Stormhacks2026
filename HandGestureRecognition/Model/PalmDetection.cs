using HandGestureRecognition.Utils;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace HandGestureRecognition.Model;

/// <summary>One detected palm, all values normalized to the frame (rotation in radians).</summary>
public readonly record struct PalmHand(double SqnRrSize, double Rotation, double SqnRrCenterX, double SqnRrCenterY);

/// <summary>Port of model/palm_detection/palm_detection.py.</summary>
public sealed class PalmDetection : IDisposable
{
    private readonly InferenceSession _session;
    /// <summary>True if the model runs on the GPU (DirectML); false if on the CPU (asked for, or fallback).</summary>
    public bool OnGpu { get; }
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private readonly int _inputH;
    private readonly int _inputW;
    private readonly float _scoreThreshold;

    private int _squareStandardSize;
    private int _squarePaddingHalfSize;

    public PalmDetection(
        string modelPath = "model/palm_detection/palm_detection_full_inf_post_192x192.onnx",
        float scoreThreshold = 0.60f,
        int? gpuAdapter = null)
    {
        _scoreThreshold = scoreThreshold;
        _session = OnnxSessionFactory.Create(modelPath, gpuAdapter, out bool onGpu);
        OnGpu = onGpu;
        _inputName = _session.InputNames[0];
        var dims = _session.InputMetadata[_inputName].Dimensions; // [1, 3, 192, 192]
        _inputH = dims[2];
        _inputW = dims[3];
        _outputNames = _session.OutputNames.ToArray();
    }

    /// <summary>Detects palms in a full BGR frame.</summary>
    public List<PalmHand> Run(Mat image)
    {
        float[] input = Preprocess(image);

        using var runOptions = new RunOptions();
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, _inputH, _inputW });
        using var outputs = _session.Run(runOptions, new[] { _inputName }, new[] { inputValue }, _outputNames);

        // pdscore_boxx_boxy_boxsize_kp0x_kp0y_kp2x_kp2y : float32[N, 8]
        var boxesValue = outputs.First();
        long[] shape = boxesValue.GetTensorTypeAndShape().Shape;
        float[] boxes = boxesValue.GetTensorDataAsSpan<float>().ToArray();
        int count = shape.Length > 0 ? (int)shape[0] : 0;

        return Postprocess(image, boxes, count);
    }

    private float[] Preprocess(Mat image)
    {
        int imageHeight = image.Rows;
        int imageWidth = image.Cols;

        _squareStandardSize = Math.Max(imageHeight, imageWidth);
        _squarePaddingHalfSize = Math.Abs(imageHeight - imageWidth) / 2;

        var (padded, resized) = ImageUtils.KeepAspectResizeAndPad(image, _inputW, _inputH);
        using (padded)
        using (resized)
        {
            var tensor = new float[3 * _inputH * _inputW];
            ImageUtils.BgrToRgbChwNormalized(padded, tensor, 0);
            return tensor;
        }
    }

    private List<PalmHand> Postprocess(Mat image, float[] boxes, int count)
    {
        int imageHeight = image.Rows;
        var hands = new List<PalmHand>();

        for (int i = 0; i < count; i++)
        {
            int o = i * 8;
            float pdScore = boxes[o];
            if (!(pdScore > _scoreThreshold))
            {
                continue;
            }

            double boxX = boxes[o + 1];
            double boxY = boxes[o + 2];
            double boxSize = boxes[o + 3];
            double kp0X = boxes[o + 4];
            double kp0Y = boxes[o + 5];
            double kp2X = boxes[o + 6];
            double kp2Y = boxes[o + 7];

            if (boxSize > 0)
            {
                double kp02X = kp2X - kp0X;
                double kp02Y = kp2Y - kp0Y;
                double sqnRrSize = 2.9 * boxSize;
                double rotation = 0.5 * Math.PI - Math.Atan2(-kp02Y, kp02X);
                rotation = ImageUtils.NormalizeRadians(rotation);
                double sqnRrCenterX = boxX + 0.5 * boxSize * Math.Sin(rotation);
                double sqnRrCenterY = boxY - 0.5 * boxSize * Math.Cos(rotation);
                sqnRrCenterY = (sqnRrCenterY * _squareStandardSize - _squarePaddingHalfSize) / imageHeight;

                hands.Add(new PalmHand(sqnRrSize, rotation, sqnRrCenterX, sqnRrCenterY));
            }
        }

        return hands;
    }

    public void Dispose() => _session.Dispose();
}
