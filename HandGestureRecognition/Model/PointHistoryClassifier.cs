using Microsoft.ML.OnnxRuntime;

namespace HandGestureRecognition.Model;

/// <summary>Port of model/point_history_classifier/point_history_classifier.py (finger gesture LSTM).</summary>
public sealed class PointHistoryClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly float _scoreTh;

    public PointHistoryClassifier(
        string modelPath = "model/point_history_classifier/point_history_classifier_lstm.onnx",
        float scoreTh = 0.5f,
        bool tryCuda = true)
    {
        _session = OnnxSessionFactory.Create(modelPath, tryCuda);
        _inputNames = _session.InputNames.ToArray();   // input [batch, 32], score_threshold (scalar)
        _outputNames = _session.OutputNames.ToArray(); // class_ids int64 [batch]
        _scoreTh = scoreTh;
    }

    /// <summary>pointHistory: [N, 32] relative index-finger trajectories. Returns class IDs [N].</summary>
    public long[] Run(IReadOnlyList<double[]> pointHistory)
    {
        if (pointHistory.Count == 0)
        {
            return Array.Empty<long>();
        }

        int n = pointHistory.Count;
        int width = pointHistory[0].Length;
        var input = new float[n * width];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < width; j++)
            {
                input[i * width + j] = (float)pointHistory[i][j];
            }
        }

        using var runOptions = new RunOptions();
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { n, width });
        using var scoreValue = OrtValue.CreateTensorValueFromMemory(new[] { _scoreTh }, Array.Empty<long>());
        using var outputs = _session.Run(
            runOptions,
            new[] { _inputNames[0], _inputNames[1] },
            new[] { inputValue, scoreValue },
            _outputNames);
        return outputs.First().GetTensorDataAsSpan<long>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
