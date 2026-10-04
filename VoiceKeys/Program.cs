using VoiceKeys;

if (args.Contains("--list-mics"))
{
    foreach (var (index, name) in MicCapture.ListDevices())
        Console.WriteLine($"{index}: {name}");
    return 0;
}

AppConfig cfg;
try { cfg = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"), Path.Combine(AppContext.BaseDirectory, "presets")); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Couldn't load config: {ex.Message}");
    return 1;
}

// --preset <id> overrides the starting preset from settings.json.
var presetArg = Array.IndexOf(args, "--preset");
var startId = presetArg >= 0 && presetArg + 1 < args.Length ? args[presetArg + 1] : cfg.Preset;
var preset = cfg.FindPreset(startId);
if (preset is null)
{
    Console.Error.WriteLine($"No preset \"{startId}\"; have: {string.Join(", ", cfg.Presets.Select(p => p.Id))}");
    return 1;
}
var matcher = new CommandMatcher(cfg, preset);

// Offline check of what a sentence would do, without a mic, API key, or pressing anything:
//   dotnet run -- --try "copy then paste" "I'll copy that later"
//   dotnet run -- --preset rocket-league --try "drive" "flip left"
var tryArg = Array.IndexOf(args, "--try");
if (tryArg >= 0)
{
    var phrases = args.Skip(tryArg + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
    Console.WriteLine($"[{preset.Name}]");
    foreach (var phrase in phrases)
        Console.WriteLine($"{phrase,-30} → {matcher.Match(phrase, paused: false) switch
        {
            MatchResult.Commands c => string.Join("  |  ", c.Items.Select(i => i.Describe())),
            MatchResult.Dictation d => $"type \"{d.Text}\"",
            MatchResult.SwitchPreset s => $"switch to {s.Preset.Name}",
            MatchResult.Pause => "pause",
            MatchResult.Resume => "resume",
            _ => "(nothing)",
        }}");
    return 0;
}

var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set the ELEVENLABS_API_KEY environment variable first.");
    return 1;
}

// End-to-end check of the ElevenLabs pipeline without a mic or pressing keys:
// text-to-speech speaks each phrase, the audio is streamed into realtime speech-to-text exactly
// like push-to-talk would, and the transcript runs through the matcher.
//   dotnet run -- --selftest "next tab" "go back then up"
var selftestArg = Array.IndexOf(args, "--selftest");
if (selftestArg >= 0)
{
    using var tts = new Speaker(apiKey, cfg.Speech);
    var phrases = args.Skip(selftestArg + 1).TakeWhile(a => !a.StartsWith("--")).DefaultIfEmpty("go back then next tab");
    foreach (var phrase in phrases)
    {
        var pcm = await tts.SynthesizeAsync(phrase, MicCapture.SampleRate, CancellationToken.None);
        await using var stt = new ScribeClient();
        var done = new TaskCompletionSource<string>();
        var firstPartialMs = -1L;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        stt.PartialTranscript += t => { if (firstPartialMs < 0 && t.Length > 0) firstPartialMs = clock.ElapsedMilliseconds; };
        long committedAt = -1;
        stt.CommittedTranscript += t => { committedAt = clock.ElapsedMilliseconds; done.TrySetResult(t); };
        stt.Error += (type, details) => done.TrySetException(new Exception($"{type}: {details}"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ptt = cfg.PushToTalk;
        await stt.ConnectAsync(apiKey, cfg.Scribe, manualCommit: ptt.Enabled, matcher.AllPhrases, timeout.Token);
        var run = stt.RunAsync(timeout.Token);

        // Feed it in real time, 50 ms chunks, the way the configured mode would:
        //   push-to-talk  pad with the silence it adds (prerollMs before, tailMs after), then commit like a release
        //   open mic      trail off into silence and let ElevenLabs' voice detection commit it
        int lead = MicCapture.SampleRate * 2 * (ptt.Enabled ? ptt.PrerollMs : 300) / 1000;
        int speechEnd = lead + pcm.Length;
        pcm = new byte[lead]
            .Concat(pcm)
            .Concat(new byte[MicCapture.SampleRate * 2 * (ptt.Enabled ? ptt.TailMs : 1500) / 1000])
            .ToArray();
        int chunk = MicCapture.SampleRate / 20 * 2;
        clock.Restart();
        long speechEndMs = -1;
        for (int off = 0; off < pcm.Length; off += chunk)
        {
            if (speechEndMs < 0 && off >= speechEnd) speechEndMs = clock.ElapsedMilliseconds;
            stt.SendAudio(pcm[off..Math.Min(off + chunk, pcm.Length)]);
            await Task.Delay(50);
        }
        var audioEndMs = clock.ElapsedMilliseconds;
        if (ptt.Enabled) stt.SendAudio(new byte[320], commit: true);

        try
        {
            var text = await done.Task.WaitAsync(timeout.Token);
            var result = matcher.Match(text, paused: false);
            Console.WriteLine($"said \"{phrase}\" → heard \"{text}\" → " + (result is MatchResult.Commands c
                ? string.Join("  |  ", c.Items.Select(i => i.Describe())) : result.GetType().Name));
            Console.WriteLine(ptt.Enabled
                ? $"   first partial {firstPartialMs} ms into audio · committed {clock.ElapsedMilliseconds - audioEndMs} ms after release"
                : $"   first partial {firstPartialMs} ms into audio · committed {committedAt - speechEndMs} ms after you stopped speaking");
        }
        catch (Exception ex) { Console.WriteLine($"said \"{phrase}\" → FAILED: {ex.Message}"); }
    }
    return 0;
}

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
// Never leave a latched key (like "drive" holding W) stuck down after we exit.
AppDomain.CurrentDomain.ProcessExit += (_, _) => KeySender.ReleaseEverything();

await using var engine = new VoiceEngine(cfg, apiKey, preset);
engine.Partial += text => { if (text.Length == 0) ClearStatus(); else WriteStatus($"… {text}"); };
engine.Log += entry =>
{
    ClearStatus();
    var line = entry.Kind switch
    {
        VoiceLogKind.Command => $"\"{entry.Text}\" → {entry.Action}",
        VoiceLogKind.Early => $"⚡ \"{entry.Text}\" → {entry.Action}",
        VoiceLogKind.Dictation => $"\"{entry.Text}\" → {entry.Action}",
        VoiceLogKind.Preset => $"→ {entry.Text} preset ({entry.Action})",
        VoiceLogKind.Ignored => $"\"{entry.Text}\" ({entry.Action ?? "no command"})",
        VoiceLogKind.Error => $"  ! {entry.Text}",
        _ => entry.Text,
    };
    if (entry.Kind == VoiceLogKind.Ignored) Console.ForegroundColor = ConsoleColor.DarkGray;
    (entry.Kind == VoiceLogKind.Error ? Console.Error : Console.Out).WriteLine(line);
    Console.ResetColor();
};

Console.WriteLine("Presets: " + string.Join(", ", engine.SwitchablePresets.Select(p => $"{p.Name} (\"{p.SwitchPhrases[0]}\")")) + ". Ctrl+C to quit.");
await engine.RunAsync(quit.Token);
Console.WriteLine("\nBye.");
return 0;

static void WriteStatus(string text)
{
    var width = Math.Max(10, Console.WindowWidth - 1);
    Console.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
}

static void ClearStatus() => Console.Write("\r" + new string(' ', Math.Max(10, Console.WindowWidth - 1)) + "\r");
