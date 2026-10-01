using System.Text;
using NAudio.Wave;
using Windows.Media.SpeechRecognition;
using WinRecognizer = Windows.Media.SpeechRecognition.SpeechRecognizer;

namespace Otto;

/// Push-to-talk dictation, best engine first:
///  1. Windows' online dictation (the engine behind Win+H): free and accurate, but needs "Online speech
///     recognition" switched on in Windows privacy settings.
///  2. Otherwise, record the clip and have the AI provider transcribe it (Gemini and OpenAI can; Claude can't).
/// The old offline Windows engine is no longer used: its free dictation is too inaccurate to be worth it.
sealed class Voice : IDisposable
{
    WinRecognizer? online;
    readonly StringBuilder heard = new();
    TaskCompletionSource? stopped;
    // 16 kHz 16-bit mono: speech quality, small upload. 32,000 bytes = 1 second of audio.
    const int SampleRate = 16_000, BytesPerSecond = SampleRate * 2;
    const int MaxSeconds = 120; // a forgotten mic can't make a huge upload
    WaveInEvent? mic;
    MemoryStream? clip;
    WaveFileWriter? writer;
    enum Mode { None, Online, Record }
    Mode mode;

    public bool Listening { get; private set; }
    /// Why the best engine isn't in use (shown once in the panel), or null.
    public string? Note { get; private set; }
    /// True when Windows' online speech setting is what's missing (the panel offers to open it).
    public bool NeedsWindowsSetting { get; private set; }

    public async Task StartAsync()
    {
        lock (heard) heard.Clear();
        stopped = new TaskCompletionSource();
        Note = null;
        NeedsWindowsSetting = false;
        try
        {
            online ??= await CreateOnline();
            await online.ContinuousRecognitionSession.StartAsync();
            mode = Mode.Online;
        }
        catch (Exception e)
        {
            online?.Dispose();
            online = null;
            NeedsWindowsSetting = (uint)e.HResult == 0x80045509; // "Online speech recognition" is off
            var cfg = Providers.Current();
            if (!Llm.CanTranscribe(cfg))
            {
                throw new InvalidOperationException(NeedsWindowsSetting
                    ? "Voice needs Windows' online speech recognition, which is off. Turn it on in Settings → Privacy → Speech (I've opened it), then try again."
                    : "Windows' speech recognition couldn't start, and your AI provider can't transcribe audio. Try Gemini or OpenAI, or Win+H.");
            }
            Note = NeedsWindowsSetting
                ? $"Windows' online speech recognition is off, so {cfg.Provider.Label} is transcribing your voice instead (uses a little of your API allowance). Turning it on in Settings → Privacy → Speech is free."
                : $"Using {cfg.Provider.Label} to transcribe your voice.";
            StartRecording();
            mode = Mode.Record;
        }
        Listening = true;
    }

    public async Task<string> StopAsync(CancellationToken ct = default)
    {
        if (!Listening) return "";
        Listening = false;
        if (mode == Mode.Online && online != null)
        {
            try { await online.ContinuousRecognitionSession.StopAsync(); } // lets the current phrase finish
            catch { }
            await Task.WhenAny(stopped!.Task, Task.Delay(3000, ct));
            lock (heard) return heard.ToString().Trim();
        }
        if (mode == Mode.Record)
        {
            var wav = StopRecording();
            if (wav == null || wav.Length < BytesPerSecond / 2) return ""; // under half a second: nothing said
            return await Llm.TranscribeAsync(Providers.Current(), wav, ct);
        }
        return "";
    }

    public void Abort()
    {
        Listening = false;
        try
        {
            if (mode == Mode.Online) _ = online?.ContinuousRecognitionSession.CancelAsync();
            else StopRecording();
        }
        catch { }
    }

    // ---- recording (for provider transcription) ----

    void StartRecording()
    {
        clip = new MemoryStream();
        var format = new WaveFormat(SampleRate, 16, 1);
        writer = new WaveFileWriter(new IgnoreDisposeStream(clip), format);
        mic = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 50 };
        mic.DataAvailable += (_, a) =>
        {
            if (writer != null && writer.Length < BytesPerSecond * MaxSeconds) writer.Write(a.Buffer, 0, a.BytesRecorded);
        };
        mic.StartRecording();
    }

    byte[]? StopRecording()
    {
        if (mic == null) return null;
        mic.StopRecording();
        mic.Dispose();
        mic = null;
        writer?.Dispose(); // finalises the WAV header
        writer = null;
        var bytes = clip?.ToArray();
        clip = null;
        return bytes;
    }

    sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long v) => inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        protected override void Dispose(bool disposing) { } // the WAV writer closes its stream; keep ours
    }

    // ---- Windows online dictation ----

    async Task<WinRecognizer> CreateOnline()
    {
        var r = new WinRecognizer();
        r.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        var compiled = await r.CompileConstraintsAsync();
        if (compiled.Status != SpeechRecognitionResultStatus.Success)
        {
            r.Dispose();
            throw new InvalidOperationException("dictation unavailable: " + compiled.Status);
        }
        // long pauses mid-sentence shouldn't cut you off; the mic key ends it
        r.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(2);
        r.ContinuousRecognitionSession.ResultGenerated += (_, a) =>
        {
            if (a.Result.Status == SpeechRecognitionResultStatus.Success && a.Result.Text.Length > 0)
                lock (heard) heard.Append(a.Result.Text).Append(' ');
        };
        r.ContinuousRecognitionSession.Completed += (_, _) => stopped?.TrySetResult();
        return r;
    }

    public void Dispose()
    {
        online?.Dispose();
        StopRecording();
    }
}

/// Reads replies aloud with Windows' natural-sounding voices (the ones Narrator uses), if turned on in settings.
static class Speaker
{
    static Windows.Media.SpeechSynthesis.SpeechSynthesizer? synth;
    static Windows.Media.Playback.MediaPlayer? player;

    public static async void Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            synth ??= new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            player ??= new Windows.Media.Playback.MediaPlayer();
            var stream = await synth.SynthesizeTextToStreamAsync(text.Length > 1500 ? text[..1500] : text);
            player.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
        }
        catch { /* no voices / no audio device: just stay quiet */ }
    }

    public static void Stop()
    {
        try { player?.Pause(); } catch { }
    }
}
