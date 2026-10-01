using System.Globalization;
using System.Speech.Recognition;
using System.Text;
using Windows.Media.SpeechRecognition;
using WinRecognizer = Windows.Media.SpeechRecognition.SpeechRecognizer;

namespace Otto;

/// Push-to-talk dictation. Uses Windows 10's online dictation engine (the one behind Win+H), which is far
/// more accurate than the old offline one; falls back to the offline engine if online speech recognition is
/// switched off in Windows privacy settings or unavailable.
sealed class Voice : IDisposable
{
    WinRecognizer? online;
    SpeechRecognitionEngine? offline;
    readonly StringBuilder heard = new();
    TaskCompletionSource? stopped;
    bool usingOnline;

    public bool Listening { get; private set; }
    /// Set when the online engine couldn't start, so the panel can say why accuracy dropped.
    public string? FallbackReason { get; private set; }

    public async Task StartAsync()
    {
        lock (heard) heard.Clear();
        stopped = new TaskCompletionSource();
        try
        {
            online ??= await CreateOnline();
            await online.ContinuousRecognitionSession.StartAsync();
            usingOnline = true;
            FallbackReason = null;
        }
        catch (Exception e)
        {
            // 0x80045509: "Online speech recognition" is off in Settings → Privacy → Speech
            FallbackReason = (uint)e.HResult == 0x80045509
                ? "Online speech recognition is off in Windows (Settings → Privacy → Speech), so I'm using the older, less accurate one."
                : "Couldn't start the better speech recogniser, so I'm using the older one.";
            online?.Dispose();
            online = null;
            offline ??= CreateOffline();
            offline.RecognizeAsync(RecognizeMode.Multiple);
            usingOnline = false;
        }
        Listening = true;
    }

    public async Task<string> StopAsync()
    {
        if (!Listening) return "";
        Listening = false;
        if (usingOnline && online != null)
        {
            try { await online.ContinuousRecognitionSession.StopAsync(); } // lets the current phrase finish
            catch { }
        }
        else offline?.RecognizeAsyncStop();
        await Task.WhenAny(stopped!.Task, Task.Delay(3000));
        lock (heard) return heard.ToString().Trim();
    }

    public void Abort()
    {
        Listening = false;
        try
        {
            if (usingOnline) _ = online?.ContinuousRecognitionSession.CancelAsync();
            else offline?.RecognizeAsyncCancel();
        }
        catch { }
    }

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

    SpeechRecognitionEngine CreateOffline()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var info = installed.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture))
                ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                ?? installed.FirstOrDefault()
                ?? throw new InvalidOperationException("No Windows speech recognizer installed (Settings → Time & Language → Speech).");
        var e = new SpeechRecognitionEngine(info);
        e.LoadGrammar(new DictationGrammar());
        e.SetInputToDefaultAudioDevice();
        e.SpeechRecognized += (_, a) => { lock (heard) heard.Append(a.Result.Text).Append(' '); };
        e.RecognizeCompleted += (_, _) => stopped?.TrySetResult();
        return e;
    }

    public void Dispose()
    {
        online?.Dispose();
        offline?.Dispose();
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
