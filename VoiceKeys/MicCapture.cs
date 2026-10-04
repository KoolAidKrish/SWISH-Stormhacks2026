using NAudio.Wave;

namespace VoiceKeys;

/// <summary>
/// Captures the default microphone as 16 kHz / 16-bit / mono PCM, which is exactly
/// the "pcm_16000" format Scribe expects, so no resampling is needed.
/// </summary>
public sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;

    private readonly WaveInEvent _waveIn;

    /// <summary>Raised roughly every <c>chunkMs</c> with a fresh PCM buffer.</summary>
    public event Action<byte[]>? ChunkAvailable;

    public MicCapture(int deviceNumber = 0, int chunkMs = 100)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = chunkMs,
            NumberOfBuffers = 3,
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            // NAudio reuses its buffer, so copy before handing it off.
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            ChunkAvailable?.Invoke(copy);
        };
    }

    public static IEnumerable<(int Index, string Name)> ListDevices()
    {
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            yield return (i, WaveInEvent.GetCapabilities(i).ProductName);
    }

    public void Start() => _waveIn.StartRecording();
    public void Stop() => _waveIn.StopRecording();
    public void Dispose() => _waveIn.Dispose();
}
