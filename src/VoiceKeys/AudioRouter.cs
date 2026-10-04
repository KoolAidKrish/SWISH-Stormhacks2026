namespace VoiceKeys;

/// <summary>
/// Decides which mic chunks go to ElevenLabs.
///
/// Push-to-talk: while idle, the last few chunks are kept in a small pre-roll buffer
/// (people start talking a beat before the key is fully down). On press, the pre-roll is
/// flushed and audio streams live. On release, a short tail keeps streaming (people let go
/// slightly early), then the final chunk is sent with commit=true so the transcript is
/// finalized immediately instead of after a silence timeout.
///
/// Open mic: everything streams, except while a spoken confirmation is playing, so the
/// app can't hear its own voice and trigger itself.
/// </summary>
public sealed class AudioRouter
{
    private enum State { Idle, Live, Tail }

    private readonly object _lock = new();
    private readonly Func<ScribeClient?> _target;
    private readonly Func<bool> _suppressOpenMic;
    private readonly bool _pushToTalk;
    private readonly int _prerollChunks, _tailChunks;
    private readonly Queue<byte[]> _preroll = new();
    private State _state = State.Idle;
    private int _tailLeft;

    /// <summary>A short silent chunk used to commit when there's no audio left to attach the commit to.</summary>
    private static readonly byte[] Silence10Ms = new byte[MicCapture.SampleRate / 100 * 2];

    public AudioRouter(Func<ScribeClient?> target, Func<bool> suppressOpenMic, bool pushToTalk, int chunkMs, int prerollMs, int tailMs)
    {
        _target = target;
        _suppressOpenMic = suppressOpenMic;
        _pushToTalk = pushToTalk;
        _prerollChunks = Math.Max(0, prerollMs / chunkMs);
        _tailChunks = Math.Max(0, tailMs / chunkMs);
    }

    public bool IsTransmitting { get { lock (_lock) return _state != State.Idle; } }

    public void OnChunk(byte[] pcm)
    {
        if (!_pushToTalk)
        {
            if (!_suppressOpenMic()) _target()?.SendAudio(pcm);
            return;
        }

        lock (_lock)
        {
            switch (_state)
            {
                case State.Idle:
                    _preroll.Enqueue(pcm);
                    while (_preroll.Count > _prerollChunks) _preroll.Dequeue();
                    break;
                case State.Live:
                    _target()?.SendAudio(pcm);
                    break;
                case State.Tail:
                    var last = --_tailLeft <= 0;
                    _target()?.SendAudio(pcm, commit: last);
                    if (last) _state = State.Idle;
                    break;
            }
        }
    }

    public void Press()
    {
        lock (_lock)
        {
            if (_state == State.Idle)
            {
                var client = _target();
                while (_preroll.TryDequeue(out var chunk)) client?.SendAudio(chunk);
            }
            _state = State.Live; // pressing again during the tail just continues the same utterance
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_state != State.Live) return;
            if (_tailChunks > 0) { _state = State.Tail; _tailLeft = _tailChunks; }
            else { _target()?.SendAudio(Silence10Ms, commit: true); _state = State.Idle; }
        }
    }
}
