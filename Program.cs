namespace Otto;

static class Program
{
    static bool WaitForMutex(Mutex m)
    {
        try { return m.WaitOne(TimeSpan.FromSeconds(60)); }
        catch (AbandonedMutexException) { return true; } // the old copy exited without releasing it: ours now
    }

    [STAThread]
    static void Main()
    {
        // developers: OTTO_DATA points Otto at a separate data folder (sample chats, reminders...) instead of the real one
        if (CommandLine.Dev && Environment.GetEnvironmentVariable("OTTO_DATA") is { Length: > 0 } data) Paths.Data = data;
        Migration.FromJarvis(); // used to be called Jarvis; carry its data over once
        if (CommandLine.Run()) return;
        using var mutex = new Mutex(true, "Otto.SingleInstance", out bool first);
        bool updated = Environment.GetCommandLineArgs().Contains("--updated");
        // right after an update the previous copy is still closing: wait for it instead of quitting
        if (!first && updated && !WaitForMutex(mutex)) return;
        // started again while Otto is running: show the running one instead of silently doing nothing
        if (!first && !updated && !SingleInstance.TakeOver(mutex)) return;
        Updater.CleanUpAfterUpdate();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
