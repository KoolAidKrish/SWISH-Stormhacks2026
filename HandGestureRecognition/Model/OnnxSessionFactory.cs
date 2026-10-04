using Microsoft.ML.OnnxRuntime;

namespace HandGestureRecognition.Model;

/// <summary>
/// Creates InferenceSessions the way the Python code does:
/// log_severity_level = 3 (ERROR) and providers = [CUDA, CPU] with silent fallback.
/// </summary>
internal static class OnnxSessionFactory
{
    public static InferenceSession Create(string modelPath, bool tryCuda = true)
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

        if (tryCuda)
        {
            try
            {
                // Only succeeds with the Microsoft.ML.OnnxRuntime.Gpu package + CUDA installed.
                options.AppendExecutionProvider_CUDA(0);
            }
            catch (Exception)
            {
                // Fall back to the CPU provider, like onnxruntime's provider list in Python.
            }
        }

        return new InferenceSession(modelPath, options);
    }
}
