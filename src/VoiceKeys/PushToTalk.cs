using System.Runtime.InteropServices;

namespace VoiceKeys;

/// <summary>
/// Watches one key or mouse button globally (works while a game has focus) by polling
/// GetAsyncKeyState every 10 ms. Polling a single key is effectively free, and unlike a
/// low-level keyboard hook it can't add input lag to the game or upset anti-cheat.
/// The key still reaches the game, so pick one the game doesn't use.
/// </summary>
public sealed class PushToTalk : IDisposable
{
    private readonly int _vk;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public event Action? Pressed;
    public event Action? Released;

    public PushToTalk(string keyName)
    {
        _vk = ParseKey(keyName);
        _thread = new Thread(Poll) { IsBackground = true, Name = "PushToTalk" };
    }

    public void Start() => _thread.Start();

    private void Poll()
    {
        bool down = false;
        while (_running)
        {
            bool now = (GetAsyncKeyState(_vk) & 0x8000) != 0;
            if (now != down)
            {
                down = now;
                if (down) Pressed?.Invoke(); else Released?.Invoke();
            }
            Thread.Sleep(10);
        }
    }

    public static int ParseKey(string name) => KeySender.ParseChord(name) is [var single] ? single
        : throw new FormatException($"Push-to-talk needs exactly one key, got \"{name}\"");

    public void Dispose() => _running = false;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
