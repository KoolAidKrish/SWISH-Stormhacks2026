using Microsoft.ML.OnnxRuntime;

namespace HandGestureRecognition.Model;

/// <summary>Port of model/keypoint_classifier/keypoint_classifier.py (hand sign MLP).</summary>
public sealed class KeyPointClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;

    public KeyPointClassifier(
        string modelPath = "model/keypoint_classifier/keypoint_classifier.onnx",
        int? gpuAdapter = null)
    {
        _session = OnnxSessionFactory.Create(modelPath, gpuAdapter, out _);
        _inputName = _session.InputNames[0];
        _outputNames = _session.OutputNames.ToArray();
    }

    /// <summary>landmarks: [N, 42] pre-processed keypoints. Returns class IDs [N].</summary>
    public long[] Run(IReadOnlyList<double[]> landmarks)
    {
        if (landmarks.Count == 0)
        {
            return Array.Empty<long>();
        }

        int n = landmarks.Count;
        int width = landmarks[0].Length;
        var input = new float[n * width];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < width; j++)
            {
                input[i * width + j] = (float)landmarks[i][j];
            }
        }

        using var runOptions = new RunOptions();
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { n, width });
        using var outputs = _session.Run(runOptions, new[] { _inputName }, new[] { inputValue }, _outputNames);
        return outputs.First().GetTensorDataAsSpan<long>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
