using Microsoft.ML.OnnxRuntime;

namespace HandGestureRecognition.Model;

/// <summary>
/// Creates InferenceSessions: quiet logging (errors only), a couple of non-spinning CPU threads, and
/// optionally a GPU through DirectML (any DirectX 12 GPU: NVIDIA, AMD or Intel), falling back to the
/// CPU if that GPU can't be used.
/// </summary>
internal static class OnnxSessionFactory
{
    /// <param name="gpuAdapter">DXGI adapter index (the order IDXGIFactory::EnumAdapters lists GPUs), or null for the CPU.</param>
    /// <param name="onGpu">Whether the session ended up on the GPU.</param>
    public static InferenceSession Create(string modelPath, int? gpuAdapter, out bool onGpu)
    {
        if (gpuAdapter is { } adapter)
        {
            try
            {
                var options = CpuOptions();
                // DirectML wants these: no memory-pattern planning, one node at a time.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(adapter);
                onGpu = true;
                return new InferenceSession(modelPath, options);
            }
            catch (Exception)
            {
                // No DirectX 12 GPU at that index, or the driver refused: run on the CPU instead.
            }
        }
        onGpu = false;
        return new InferenceSession(modelPath, CpuOptions());
    }

    static SessionOptions CpuOptions()
    {
        var options = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            // These models take a few ms each; more threads don't help them, they just burn cores.
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
        };
        // By default ORT's worker threads spin-wait between runs, which kept ~8 cores busy at 30 fps.
        // Let them sleep instead: same inference speed, a fraction of the CPU.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }
}
