using System.Diagnostics;

namespace Otto;

/// Starting Otto while it's already running brings up the running one's panel. If that copy doesn't answer
/// (stuck, or an old version that can't), the user is offered to restart it, instead of the second start
/// silently doing nothing.
static class SingleInstance
{
    const string ShowName = "Otto.ShowPanel", AckName = "Otto.PanelShown";
    static EventWaitHandle? show, ack;

    /// The running copy: listen for later starts and show the panel when one asks.
    public static void Listen(Control ui, Action showPanel)
    {
        show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckName);
        var t = new Thread(() =>
        {
            while (show.WaitOne())
            {
                // the ack is set on the UI thread, so it proves the panel can actually respond
                try { ui.BeginInvoke(() => { showPanel(); ack.Set(); }); }
                catch (InvalidOperationException) { return; } // closing down
            }
        }) { IsBackground = true, Name = "Otto single instance" };
        t.Start();
    }

    /// A second start. Returns true if this copy should carry on as Otto (the old one was ended),
    /// false to quit because the running one has shown itself.
    public static bool TakeOver(Mutex mutex)
    {
        try
        {
            using var s = EventWaitHandle.OpenExisting(ShowName);
            using var a = EventWaitHandle.OpenExisting(AckName);
            s.Set();
            if (a.WaitOne(TimeSpan.FromSeconds(4))) return false;
        }
        catch (WaitHandleCannotBeOpenedException) { } // a version from before this existed
        catch (UnauthorizedAccessException) { }

        ApplicationConfiguration.Initialize();
        var answer = MessageBox.Show(
            "Otto is already running but isn't responding.\n\nRestart it? Your chats and settings are kept.",
            "Otto", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);
        if (answer != DialogResult.Yes) return false;

        using var me = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id))
            try { other.Kill(true); other.WaitForExit(5000); } catch { }
        try { return mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { return true; } // the old copy was ended holding it: ours now
    }
}
