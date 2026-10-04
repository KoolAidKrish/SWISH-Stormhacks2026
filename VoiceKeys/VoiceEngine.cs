namespace VoiceKeys;

public enum VoiceLogKind { Command, Early, Dictation, Preset, Paused, Resumed, Ignored, Info, Error }

/// <summary>One line of the voice log: what was heard and what it did.</summary>
public sealed record VoiceLogEntry(DateTime Time, VoiceLogKind Kind, string Text, string? Action = null);

/// <summary>
/// The voice pipeline: mic → (push-to-talk gate) → ElevenLabs realtime STT → matcher → KeySender,
/// plus spoken confirmations. Hosted by the console app (Program.cs) and by SWISH.App.
///
/// Transcripts arrive on the WebSocket receive thread while the host may switch preset or pause
/// from its UI thread, so the shared state below is guarded by one lock. Events are raised on
/// whatever thread produced them; a UI host marshals them to its own thread.
/// </summary>
public sealed class VoiceEngine : IAsyncDisposable
{
    private readonly AppConfig _cfg;
    private readonly string _apiKey;
    private readonly object _gate = new();
    private readonly Speaker? _speaker;
    private readonly AudioRouter _router;
    private ScribeClient? _current;
    private CancellationTokenSource? _session;

    // Guarded by _gate
    private Preset _preset;
    private CommandMatcher _matcher;
    private bool _paused;
    // How many commands of the utterance in progress already fired from partial transcripts,
    // so the final committed transcript doesn't press them a second time.
    private int _firedEarly;

    public VoiceEngine(AppConfig cfg, string apiKey, Preset startPreset)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        _preset = startPreset;
        _matcher = new CommandMatcher(cfg, startPreset);
        KeySender.DefaultHoldMs = startPreset.KeyHoldMs ?? cfg.KeyHoldMs;

        _speaker = cfg.Speech.Enabled ? new Speaker(apiKey, cfg.Speech) : null;
        var ptt = cfg.PushToTalk;
        _router = new AudioRouter(
            target: () => Volatile.Read(ref _current),
            suppressOpenMic: () => _speaker?.IsPlaying ?? false,
            pushToTalk: ptt.Enabled, cfg.ChunkMs, ptt.PrerollMs, ptt.TailMs);
    }

    public AppConfig Config => _cfg;
    public IReadOnlyList<Preset> SwitchablePresets => _cfg.Presets.Where(p => p.Switchable).ToList();
    public Preset ActivePreset { get { lock (_gate) return _preset; } }
    public bool Connected => Volatile.Read(ref _current) is not null;
    /// <summary>True while the push-to-talk key is held (audio is streaming).</summary>
    public bool Talking => _router.IsTransmitting;

    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate)
            {
                if (_paused == value) return;
                _paused = value;
                if (value) KeySender.ReleaseLatchedKeys();
            }
            Say(value ? _cfg.Speech.PausedLine : _cfg.Speech.ResumedLine);
            Emit(value ? VoiceLogKind.Paused : VoiceLogKind.Resumed, value ? "Paused" : "Listening");
            StateChanged?.Invoke();
        }
    }

    /// <summary>Every heard utterance and what it did, plus status/errors.</summary>
    public event Action<VoiceLogEntry>? Log;
    /// <summary>Live (unfinished) transcript while you speak.</summary>
    public event Action<string>? Partial;
    /// <summary>Connected/paused/preset/talking changed.</summary>
    public event Action? StateChanged;

    public void SwitchPreset(Preset next)
    {
        lock (_gate)
        {
            if (next == _preset) return;
            KeySender.ReleaseLatchedKeys(); // don't carry "drive" into the desktop
            _preset = next;
            _matcher = new CommandMatcher(_cfg, next);
            _firedEarly = 0;
            KeySender.DefaultHoldMs = next.KeyHoldMs ?? _cfg.KeyHoldMs;
        }
        Say(next.AnnounceLine);
        Emit(VoiceLogKind.Preset, next.Name, $"{next.Commands.Count} commands");
        _session?.Cancel(); // reconnect so the recognizer is biased toward the new preset's words
        StateChanged?.Invoke();
    }

    /// <summary>Runs until <paramref name="ct"/> fires: mic, push-to-talk, and the reconnecting STT session.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_speaker is not null) _ = PreloadVoiceLinesAsync(ct);

        using var mic = new MicCapture(_cfg.MicDevice, _cfg.ChunkMs);
        mic.ChunkAvailable += _router.OnChunk;
        mic.Start();

        var ptt = _cfg.PushToTalk;
        using var pushToTalk = ptt.Enabled ? new PushToTalk(ptt.Key) : null;
        if (pushToTalk is not null)
        {
            pushToTalk.Pressed += () => { _router.Press(); StateChanged?.Invoke(); };
            pushToTalk.Released += () => { _router.Release(); StateChanged?.Invoke(); };
            pushToTalk.Start();
        }

        Emit(VoiceLogKind.Info, $"{ActivePreset.Name} preset. " + (ptt.Enabled ? $"Hold {ptt.Key} to talk." : "Open mic."));

        var backoff = TimeSpan.FromSeconds(1);
        var reconnecting = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await using var client = new ScribeClient();
                client.PartialTranscript += HandlePartial;
                client.CommittedTranscript += HandleCommitted;
                client.Error += (type, details) =>
                {
                    // With push-to-talk the session sits silent between presses, so an idle disconnect is
                    // expected and we quietly reconnect. Shown only if it happens mid-press.
                    if (ptt.Enabled && !_router.IsTransmitting && type is "insufficient_audio_activity" or "closed") return;
                    Emit(VoiceLogKind.Error, $"{type}: {details}");
                };

                _session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var connectedAt = DateTime.UtcNow;
                try
                {
                    // Keyterms (recognition hints) are fixed per connection, which is why switching preset reconnects.
                    IEnumerable<string> phrases;
                    lock (_gate) phrases = _matcher.AllPhrases.ToList();
                    await client.ConnectAsync(_apiKey, _cfg.Scribe, manualCommit: ptt.Enabled, phrases, _session.Token);
                    connectedAt = DateTime.UtcNow;
                    Volatile.Write(ref _current, client);
                    if (!reconnecting) Emit(VoiceLogKind.Info, "Connected to ElevenLabs.");
                    StateChanged?.Invoke();
                    await client.RunAsync(_session.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* preset switch: reconnect right away below */ }
                catch (Exception ex) { Emit(VoiceLogKind.Error, $"Connection problem: {ex.Message}"); }
                finally
                {
                    Volatile.Write(ref _current, null);
                    StateChanged?.Invoke();
                }

                // Sessions time out when idle or hit a time limit, and networks drop. A session that lived a
                // while (or was closed on purpose) reconnects straight away so it's ready for the next press;
                // one that failed quickly (bad key, no internet) backs off so we don't hammer the API.
                if (ct.IsCancellationRequested) break;
                reconnecting = true;
                if (_session.IsCancellationRequested || DateTime.UtcNow - connectedAt > TimeSpan.FromSeconds(10))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    continue;
                }
                Emit(VoiceLogKind.Info, $"Reconnecting in {backoff.TotalSeconds:0}s…");
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
        finally
        {
            mic.Stop();
            KeySender.ReleaseEverything();
        }
    }

    private async Task PreloadVoiceLinesAsync(CancellationToken ct)
    {
        // Generate missing voice lines for every preset in the background; cached ones load from disk instantly.
        var lines = _cfg.Presets
            .SelectMany(p => p.Commands.Select(c => SpokenLine(c, p)).Append(p.AnnounceLine))
            .Append(_cfg.Speech.PausedLine).Append(_cfg.Speech.ResumedLine)
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();
        try
        {
            var made = await _speaker!.PreloadAsync(lines, ct);
            if (made > 0) Emit(VoiceLogKind.Info, $"Generated {made} voice line(s) with ElevenLabs (cached for next time).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit(VoiceLogKind.Error, $"Couldn't generate voice lines: {ex.Message}");
        }
    }

    // Partial and committed events both arrive on the WebSocket receive loop.
    private void HandlePartial(string text)
    {
        List<Invocation>? fire = null;
        lock (_gate)
        {
            if (_cfg.FireOnPartial && !_paused)
            {
                var sure = _matcher.MatchPartial(text);
                if (sure.Count > _firedEarly)
                {
                    fire = sure.Skip(_firedEarly).ToList();
                    _firedEarly = sure.Count;
                }
            }
        }
        if (fire is not null)
        {
            foreach (var inv in fire) Run(inv);
            Emit(VoiceLogKind.Early, text, Describe(fire));
        }
        Partial?.Invoke(text);
    }

    private void HandleCommitted(string transcript)
    {
        MatchResult result;
        int already;
        lock (_gate)
        {
            already = _firedEarly;
            _firedEarly = 0;
            if (string.IsNullOrWhiteSpace(transcript)) { Partial?.Invoke(""); return; }
            result = _matcher.Match(transcript, _paused);
        }
        Partial?.Invoke("");

        switch (result)
        {
            case MatchResult.Commands c:
                var rest = c.Items.Skip(already).ToList();
                if (rest.Count == 0) break; // everything already fired early
                foreach (var inv in rest) Run(inv);
                Emit(VoiceLogKind.Command, transcript, Describe(rest));
                break;
            case MatchResult.Dictation d:
                KeySender.TypeText(d.Text);
                Emit(VoiceLogKind.Dictation, transcript, $"type \"{d.Text}\"");
                break;
            case MatchResult.SwitchPreset s:
                SwitchPreset(s.Preset);
                break;
            case MatchResult.Pause:
                Paused = true;
                break;
            case MatchResult.Resume:
                Paused = false;
                break;
            default:
                Emit(VoiceLogKind.Ignored, transcript, Paused ? "paused" : null);
                break;
        }
    }

    private void Run(Invocation inv)
    {
        KeySender.Run(inv.Cmd.StepsFor(inv.Scale));
        Say(SpokenLine(inv.Cmd, ActivePreset));
    }

    private void Say(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) _speaker?.Say(line);
    }

    private string? SpokenLine(CommandDef cmd, Preset p) =>
        cmd.Speak ?? ((p.SpeakByDefault ?? _cfg.Speech.SpeakByDefault) ? cmd.Say.FirstOrDefault() : null);

    private static string Describe(IEnumerable<Invocation> invs) => string.Join("  |  ", invs.Select(i => i.Describe()));

    private void Emit(VoiceLogKind kind, string text, string? action = null) =>
        Log?.Invoke(new VoiceLogEntry(DateTime.Now, kind, text, action));

    public ValueTask DisposeAsync()
    {
        _session?.Cancel();
        _speaker?.Dispose();
        KeySender.ReleaseEverything();
        return ValueTask.CompletedTask;
    }
}
