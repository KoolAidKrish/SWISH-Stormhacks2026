using System.Text;
using System.Text.RegularExpressions;

namespace VoiceKeys;

/// <summary>A command plus how much its duration was scaled by modifiers ("hard left" = left × 2.2).</summary>
public sealed record Invocation(CommandDef Cmd, double Scale)
{
    public string Describe() => Cmd.Describe(Scale);
}

public abstract record MatchResult
{
    public sealed record Commands(IReadOnlyList<Invocation> Items) : MatchResult;
    public sealed record Dictation(string Text) : MatchResult;
    public sealed record Pause : MatchResult;
    public sealed record Resume : MatchResult;
    public sealed record SwitchPreset(Preset Preset) : MatchResult;
    public sealed record None : MatchResult;
}

/// <summary>
/// Turns a transcript into actions. The whole utterance must be accounted for:
/// every word has to belong to a command phrase or be a filler word. That way
/// "copy then paste" runs two commands, but "I'll copy that later" runs nothing,
/// which keeps ordinary talking from mashing your keyboard.
///
/// Presets can also define modifier words that scale a command's duration, read the way
/// English does: a modifier applies to the action after it ("hard left", "really little boost"),
/// or, at the very end, to the action before it ("left a little"). Intensifiers ("really")
/// exaggerate the next modifier in whichever direction it points.
/// </summary>
public sealed class CommandMatcher
{
    private sealed record Phrase(string[] Words, CommandDef Cmd, bool Ambiguous);
    private sealed record ModPhrase(string[] Words, ModifierDef Mod);

    private readonly AppConfig _cfg;
    private readonly Preset _preset;
    private readonly List<Phrase> _phrases;
    private readonly List<ModPhrase> _modifiers;
    private readonly HashSet<string> _fillers;
    private readonly HashSet<string> _dictationPrefixes;

    private readonly IReadOnlyList<CommandDef> _extra;

    /// <param name="extra">Commands that apply on top of the preset (the user's custom voice functions).</param>
    public CommandMatcher(AppConfig cfg, Preset preset, IReadOnlyList<CommandDef>? extra = null)
    {
        _cfg = cfg;
        _preset = preset;
        _fillers = cfg.FillerWords.Select(Normalize).ToHashSet();
        _dictationPrefixes = cfg.DictationPrefixes.Select(Normalize).ToHashSet();

        _extra = extra ?? [];
        var commandPhrases = preset.Commands.Concat(_extra)
            .SelectMany(c => c.Say.Select(p => (Words: Tokenize(p), Cmd: (CommandDef?)c)))
            .Where(p => p.Words.Length > 0)
            .ToList();
        // Control phrases (cmd = null) take part in the ambiguity check, so a "stop" command
        // can't fire early while you're halfway through saying "stop listening".
        var everything = commandPhrases
            .Concat(ControlPhrases.Select(p => (Words: Tokenize(p), Cmd: (CommandDef?)null)))
            .ToList();

        _phrases = commandPhrases
            .Select(p => new Phrase(p.Words, p.Cmd!, Ambiguous: everything.Any(o =>
                o.Words.Length > p.Words.Length && o.Cmd != p.Cmd && o.Words.Take(p.Words.Length).SequenceEqual(p.Words))))
            .OrderByDescending(p => p.Words.Length) // longest match wins: "close tab" before "close"
            .ToList();

        _modifiers = preset.Modifiers
            .SelectMany(m => m.Say.Select(p => new ModPhrase(Tokenize(p), m)))
            .Where(m => m.Words.Length > 0)
            .OrderByDescending(m => m.Words.Length) // "a little" before "little"
            .ToList();
    }

    /// <summary>Phrases that work in every preset: pause/resume and switching presets.</summary>
    private IEnumerable<string> ControlPhrases =>
        _cfg.PausePhrases.Concat(_cfg.ResumePhrases).Concat(_cfg.Presets.SelectMany(p => p.SwitchPhrases));

    /// <summary>
    /// Every phrase the recognizer should be biased toward, by priority: the API caps hints at
    /// 50 words and the tail gets cut, so switching/pausing comes first, then commands, then modifiers.
    /// </summary>
    public IEnumerable<string> AllPhrases =>
        ControlPhrases.Concat(_cfg.DictationPrefixes)
            .Concat(_extra.SelectMany(c => c.Say))
            .Concat(_preset.Commands.SelectMany(c => c.Say))
            .Concat(_preset.Modifiers.SelectMany(m => m.Say));

    /// <summary>Final decision on a committed (finished) utterance.</summary>
    public MatchResult Match(string transcript, bool paused)
    {
        var words = Tokenize(transcript);
        if (words.Length == 0) return new MatchResult.None();
        var joined = string.Join(' ', words);

        if (_cfg.ResumePhrases.Any(p => Normalize(p) == joined)) return new MatchResult.Resume();
        if (paused) return new MatchResult.None();
        if (_cfg.PausePhrases.Any(p => Normalize(p) == joined)) return new MatchResult.Pause();
        var target = _cfg.Presets.FirstOrDefault(p => p.SwitchPhrases.Any(s => Normalize(s) == joined));
        if (target is not null) return new MatchResult.SwitchPreset(target);

        if (words.Length > 1 && _dictationPrefixes.Contains(words[0]))
            return new MatchResult.Dictation(StripFirstWord(transcript));

        var (found, complete) = Scan(words);
        return complete && found.Count > 0
            ? new MatchResult.Commands(found.Select(f => f.Inv).ToList())
            : new MatchResult.None();
    }

    /// <summary>
    /// Commands that are already certain from a partial (still-being-spoken) transcript,
    /// so they can fire without waiting for the end-of-speech silence. Stops at the first
    /// unknown word, and holds back a phrase that might still grow into a different one
    /// ("go" while you might be saying "go back"). A scalable command also waits until
    /// another command follows it, since "left" might still become "left a little".
    /// </summary>
    public IReadOnlyList<Invocation> MatchPartial(string partial)
    {
        var words = Tokenize(partial);
        if (words.Length == 0 || _dictationPrefixes.Contains(words[0])) return [];

        var (found, _) = Scan(words);
        var sure = new List<Invocation>();
        for (int k = 0; k < found.Count; k++)
        {
            var (inv, end, ambiguous) = found[k];
            if (ambiguous && end == words.Length) break; // nothing after it yet to rule out the longer phrase
            if (k == found.Count - 1 && inv.Cmd.Scalable && _modifiers.Count > 0) break; // a trailing modifier could still change it
            sure.Add(inv);
        }
        return sure;
    }

    private (List<(Invocation Inv, int End, bool Ambiguous)> Found, bool Complete) Scan(string[] words)
    {
        var found = new List<(Invocation Inv, int End, bool Ambiguous)>();
        double scale = 1, exponent = 1;
        bool pending = false; // modifiers said since the last command, waiting for one to apply to
        int i = 0;
        while (i < words.Length)
        {
            var hit = _phrases.FirstOrDefault(p => StartsWith(words, i, p.Words));
            if (hit is not null)
            {
                i += hit.Words.Length;
                found.Add((new Invocation(hit.Cmd, hit.Cmd.Scalable ? scale : 1), i, hit.Ambiguous));
                (scale, exponent, pending) = (1, 1, false);
                continue;
            }

            var mod = _modifiers.FirstOrDefault(m => StartsWith(words, i, m.Words));
            if (mod is not null)
            {
                i += mod.Words.Length;
                if (mod.Mod.Intensify is { } power) exponent *= power; // "really" strengthens the next modifier
                else { scale *= Math.Pow(mod.Mod.Scale ?? 1, exponent); exponent = 1; }
                pending = true;
                continue;
            }

            if (_fillers.Contains(words[i])) { i++; continue; }
            return (found, false); // an unexplained word: probably not talking to us
        }

        // Modifiers left over at the end apply backwards: "left a little".
        if (pending)
        {
            if (found.Count == 0) return (found, false); // just "hard" on its own means nothing
            var (inv, end, amb) = found[^1];
            if (inv.Cmd.Scalable) found[^1] = (inv with { Scale = inv.Scale * scale }, end, amb);
        }
        return (found, true);
    }

    private static bool StartsWith(string[] words, int at, string[] phrase)
    {
        if (at + phrase.Length > words.Length) return false;
        for (int k = 0; k < phrase.Length; k++)
            if (words[at + k] != phrase[k]) return false;
        return true;
    }

    /// <summary>"Type: Hello, world." -> "Hello, world" (keeps the recognizer's casing and punctuation).</summary>
    private static string StripFirstWord(string raw)
    {
        var rest = Regex.Replace(raw.Trim(), @"^\W*[\w']+[\s,:;.\-]*", "");
        return rest.EndsWith('.') && !rest.EndsWith("..") ? rest[..^1] : rest;
    }

    private static string Normalize(string s) => string.Join(' ', Tokenize(s));

    private static string[] Tokenize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
