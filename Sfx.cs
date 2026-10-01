using System.Media;
using Microsoft.Win32;

namespace Otto;

/// Otto's UI sounds. Each one is synthesised at startup, unless a file with its name exists in
/// %LOCALAPPDATA%\Otto\sounds (send.wav, reply.wav, type.wav, takeover.wav, listen-on.wav, listen-off.wav, attention.wav,
/// error.wav), which replaces it. Quiet on purpose, and only for moments Windows itself would make a sound.
static class Sfx
{
    const int Rate = 44100;
    const string Key = @"Software\Otto";

    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto", "sounds");

    // all short two-note blips in the style of listen-on/off: rising = good/start, falling = stop/problem
    static readonly byte[] send = Load("send", (698, 0, 50), (1047, 40, 80));
    static readonly byte[] reply = Load("reply", (659, 0, 70), (988, 60, 120));
    static readonly byte[] takeover = Load("takeover", (587, 0, 60), (880, 50, 110));
    static readonly byte[] listenOn = Load("listen-on", (523, 0, 70), (784, 60, 120));
    static readonly byte[] listenOff = Load("listen-off", (784, 0, 70), (523, 60, 120));
    static readonly byte[] attention = Load("attention", (880, 0, 60), (880, 100, 100));
    static readonly byte[] error = Load("error", (440, 0, 70), (311, 60, 130));

    /// A .wav the user dropped in the sounds folder wins; otherwise the built-in tone.
    static byte[] Load(string name, params (double hz, int startMs, int lenMs)[] fallback)
    {
        try
        {
            var file = Path.Combine(Folder, name + ".wav");
            if (File.Exists(file)) return File.ReadAllBytes(file);
        }
        catch { }
        return Build(fallback);
    }

    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return (k?.GetValue("Sounds") as int? ?? 1) != 0; }
        set { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("Sounds", value ? 1 : 0); }
    }

    public static void Send() => Play(send);

    // typewriter: a few slightly different clicks so it doesn't sound like a machine gun, and at most
    // one every 55 ms however fast the text streams in
    static readonly byte[][] clicks = LoadClicks();
    static long lastClick;
    static int nextClick;

    public static void Type()
    {
        long now = Environment.TickCount64;
        if (now - lastClick < 55) return;
        lastClick = now;
        Play(clicks[nextClick++ % clicks.Length]);
    }

    static byte[][] LoadClicks()
    {
        try
        {
            var file = Path.Combine(Folder, "type.wav");
            if (File.Exists(file)) return new[] { File.ReadAllBytes(file) };
        }
        catch { }
        return new[] { Click(1, 1900), Click(2, 2300), Click(3, 1700), Click(4, 2100) };
    }

    /// A key strike: a 4 ms burst of noise for the "tick" plus a short damped tone for the body.
    static byte[] Click(int seed, double body)
    {
        var rnd = new Random(seed);
        int total = Rate * 30 / 1000;
        var mix = new double[total];
        for (int i = 0; i < total; i++)
        {
            double t = (double)i / Rate;
            double noise = (rnd.NextDouble() * 2 - 1) * Math.Exp(-i / (Rate * 0.0012));
            double tone = Math.Sin(2 * Math.PI * body * t) * Math.Exp(-i / (Rate * 0.004)) * 0.5;
            mix[i] = noise + tone;
        }
        return Wav(mix, 0.10);
    }
    public static void Reply() => Play(reply);
    public static void Takeover() => Play(takeover);
    public static void ListenOn() => Play(listenOn);
    public static void ListenOff() => Play(listenOff);
    public static void Attention() => Play(attention);
    public static void Error() => Play(error);

    static void Play(byte[] wav)
    {
        if (!Enabled) return;
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } catch { /* no audio device */ }
    }

    /// Each note: frequency (Hz), start (ms), length (ms). Sine + a soft octave, quick attack, exponential decay.
    static byte[] Build(params (double hz, int startMs, int lenMs)[] notes)
    {
        int total = notes.Max(n => n.startMs + n.lenMs) * Rate / 1000;
        var mix = new double[total];
        foreach (var (hz, startMs, lenMs) in notes)
        {
            int start = startMs * Rate / 1000, len = lenMs * Rate / 1000;
            for (int i = 0; i < len && start + i < total; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Min(1, i / (Rate * 0.004)) * Math.Exp(-5.0 * i / len);
                mix[start + i] += env * (Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t));
            }
        }

        return Wav(mix, 0.12);
    }

    static byte[] Wav(double[] mix, double gain)
    {
        int total = mix.Length;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + total * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(total * 2);
        foreach (var s in mix) w.Write((short)(Math.Clamp(s * gain, -1, 1) * short.MaxValue));
        return ms.ToArray();
    }
}
