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
        };

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
