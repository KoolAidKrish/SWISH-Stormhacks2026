using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceKeys;

/// <summary>
/// Spoken confirmations via ElevenLabs text-to-speech.
///
/// Every line is generated once, saved to %LOCALAPPDATA%\VoiceKeys\tts, and kept in memory,
/// so saying a command during a game costs no API call and no network wait. Playback goes
/// through one always-open output stream with a mixer, which avoids the ~100 ms it takes to
/// open an audio device per clip. A new line cuts off the previous one.
/// </summary>
public sealed class Speaker : IDisposable
{
    private const int SampleRate = 22050; // requested as raw pcm_22050: no MP3 decoding needed

    private readonly SpeechSettings _s;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.elevenlabs.io/") };
    private readonly ConcurrentDictionary<string, byte[]> _clips = new();
    private readonly string _cacheDir;
    private readonly MixingSampleProvider _mixer;
    private readonly WaveOutEvent _out;
    private long _playingUntilTicks;

    public Speaker(string apiKey, SpeechSettings settings)
    {
        _s = settings;
        _http.DefaultRequestHeaders.Add("xi-api-key", apiKey);
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceKeys", "tts");
        Directory.CreateDirectory(_cacheDir);

        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
        _out = new WaveOutEvent { DesiredLatency = 80 };
        _out.Init(_mixer);
        _out.Play();
    }

    /// <summary>Muted: lines are silently skipped (nothing generated or played).</summary>
    public bool Muted { get; set; }

    /// <summary>Speaks in another ElevenLabs voice from now on. Lines are re-made in it as they're needed (cached per voice).</summary>
    public void ChangeVoice(string voiceId)
    {
        if (voiceId == _s.VoiceId) return;
        _s.VoiceId = voiceId;
        _clips.Clear();
    }

    public sealed record VoiceInfo(string Id, string Name, string? Description);

    /// <summary>The voices this API key can use (premade + the user's own), from ElevenLabs.</summary>
    public async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("v1/voices", ct);
        res.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<VoiceInfo>();
        foreach (var v in doc.RootElement.GetProperty("voices").EnumerateArray())
        {
            string? Label(string key) => v.TryGetProperty("labels", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Object
                                         && l.TryGetProperty(key, out var x) ? x.GetString() : null;
            var about = string.Join(", ", new[] { Label("gender"), Label("accent"), Label("descriptive") ?? Label("description") }
                                          .Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new VoiceInfo(v.GetProperty("voice_id").GetString()!, v.GetProperty("name").GetString() ?? "?", about.Length > 0 ? about : null));
        }
        return list.OrderBy(v => v.Name).ToList();
    }

    /// <summary>True while a line is playing (plus a short echo margin).</summary>
    public bool IsPlaying => Environment.TickCount64 < Interlocked.Read(ref _playingUntilTicks);

    /// <summary>Loads every line from disk, generating the missing ones. Returns how many were generated.</summary>
    public async Task<int> PreloadAsync(IEnumerable<string> lines, CancellationToken ct)
    {
        int generated = 0;
        await Parallel.ForEachAsync(lines.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
            async (line, token) =>
            {
                if (await LoadAsync(line, token)) Interlocked.Increment(ref generated);
            });
        return generated;
    }

    /// <summary>Plays a line. Lines not preloaded are fetched first, and dropped if that takes too long to still be useful.</summary>
    public void Say(string line)
    {
        if (Muted) return;
        if (_clips.TryGetValue(line, out var pcm)) { Play(pcm); return; }
        _ = Task.Run(async () =>
        {
            var started = Environment.TickCount64;
            try { await LoadAsync(line, CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"  ! Speech failed for \"{line}\": {ex.Message}"); return; }
            if (Environment.TickCount64 - started < 1500 && _clips.TryGetValue(line, out var late)) Play(late);
        });
    }

    private void Play(byte[] pcm)
    {
        var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(SampleRate, 16, 1)).ToSampleProvider();
        lock (_mixer)
        {
            _mixer.RemoveAllMixerInputs();
            _mixer.AddMixerInput(new VolumeSampleProvider(source) { Volume = (float)_s.Volume });
        }
        var durationMs = pcm.Length * 1000L / (SampleRate * 2);
        Interlocked.Exchange(ref _playingUntilTicks, Environment.TickCount64 + durationMs + _out.DesiredLatency + 250);
    }

    /// <returns>true if the line had to be generated (cost API credits), false if it came from cache.</returns>
    private async Task<bool> LoadAsync(string line, CancellationToken ct)
    {
        if (_clips.ContainsKey(line)) return false;

        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{_s.VoiceId}|{_s.ModelId}|{line}")))[..16];
        var path = Path.Combine(_cacheDir, $"{key}.pcm");
        if (File.Exists(path))
        {
            _clips[line] = await File.ReadAllBytesAsync(path, ct);
            return false;
        }

        var pcm = await SynthesizeAsync(line, SampleRate, ct);
        await File.WriteAllBytesAsync(path, pcm, ct);
        _clips[line] = pcm;
        return true;
    }

    /// <summary>Raw 16-bit mono PCM of <paramref name="text"/> at the given rate (no caching).</summary>
    public async Task<byte[]> SynthesizeAsync(string text, int sampleRate, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(
            $"v1/text-to-speech/{_s.VoiceId}?output_format=pcm_{sampleRate}",
            new { text, model_id = _s.ModelId }, ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync(ct)}");
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        _out.Dispose();
        _http.Dispose();
    }
}
