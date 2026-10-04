using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceKeys;

/// <summary>
/// One realtime speech-to-text session against ElevenLabs Scribe
/// (wss://api.elevenlabs.io/v1/speech-to-text/realtime by default).
///
/// Audio goes up as base64 "input_audio_chunk" messages. An utterance ends ("committed_transcript")
/// either when the server hears enough silence (commit_strategy=vad) or when we send a chunk with
/// commit=true (commit_strategy=manual, used for push-to-talk). In between it streams
/// "partial_transcript" guesses.
/// </summary>
public sealed class ScribeClient : IAsyncDisposable
{
    private const int MaxKeyterms = 50; // server rejects the session above this

    private readonly ClientWebSocket _ws = new();
    // ClientWebSocket forbids concurrent sends, so mic chunks are queued and sent by one loop.
    private readonly Channel<(byte[] Pcm, bool Commit)> _outgoing = Channel.CreateBounded<(byte[], bool)>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

    public event Action<string>? PartialTranscript;
    public event Action<string>? CommittedTranscript;
    /// <summary>(message_type, details) for anything that went wrong.</summary>
    public event Action<string, string>? Error;

    public async Task ConnectAsync(string apiKey, ScribeSettings s, bool manualCommit, IEnumerable<string> keyterms, CancellationToken ct)
    {
        var q = new List<string>
        {
            $"model_id={Uri.EscapeDataString(s.ModelId)}",
            $"audio_format=pcm_{MicCapture.SampleRate}",
        };
        if (manualCommit)
            q.Add("commit_strategy=manual");
        else
        {
            q.Add("commit_strategy=vad");
            q.Add($"vad_silence_threshold_secs={s.VadSilenceSecs.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(s.LanguageCode))
            q.Add($"language_code={Uri.EscapeDataString(s.LanguageCode)}");
        if (s.UseKeyterms)
            // Biases recognition toward your command words ("paste" not "taste").
            // The API allows at most 50, so send distinct words rather than whole phrases: "flip", "front",
            // "left" cover every flip variant in 3 slots instead of 8.
            foreach (var term in keyterms
                .SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(w => w.ToLowerInvariant())
                .Where(w => w.Length > 1)
                .Distinct()
                .Take(MaxKeyterms))
                q.Add($"keyterms={Uri.EscapeDataString(term)}");

        _ws.Options.SetRequestHeader("xi-api-key", apiKey);
        await _ws.ConnectAsync(new Uri($"{s.Endpoint}?{string.Join('&', q)}"), ct);
    }

    /// <summary>Queue a PCM chunk; safe to call from any thread. commit=true ends the utterance now.</summary>
    public void SendAudio(byte[] pcm, bool commit = false) => _outgoing.Writer.TryWrite((pcm, commit));

    /// <summary>Runs the send and receive loops until the socket closes or ct fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = SendLoop(linked.Token);
        var recv = ReceiveLoop(linked.Token);
        await Task.WhenAny(send, recv);
        linked.Cancel(); // if one side dies, take the other down too
        try { await Task.WhenAll(send, recv); } catch (OperationCanceledException) { }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        await foreach (var (pcm, commit) in _outgoing.Reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(new
            {
                message_type = "input_audio_chunk",
                audio_base_64 = Convert.ToBase64String(pcm),
                commit,
                sample_rate = MicCapture.SampleRate,
            });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Error?.Invoke("closed", $"{result.CloseStatus} {result.CloseStatusDescription}".Trim());
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            HandleMessage(message.ToArray());
            message.SetLength(0);
        }
    }

    private void HandleMessage(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8);
        var root = doc.RootElement;
        var type = root.TryGetProperty("message_type", out var t) ? t.GetString() : null;
        string Text() => root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";

        switch (type)
        {
            case "session_started":
                break;
            case "partial_transcript":
                PartialTranscript?.Invoke(Text());
                break;
            case "committed_transcript":
            case "committed_transcript_with_timestamps":
                CommittedTranscript?.Invoke(Text());
                break;
            default:
                // All error types (auth_error, quota_exceeded, input_error, ...) carry an "error" field.
                if (root.TryGetProperty("error", out var err))
                    Error?.Invoke(type ?? "error", err.ToString());
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
            catch { /* best effort */ }
        }
        _ws.Dispose();
    }
}
