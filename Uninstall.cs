using System.Diagnostics;
using Microsoft.Win32;

namespace Otto;

/// Tray → Uninstall Otto…: removes start-with-Windows, shortcuts and the program, and optionally everything
/// Otto saved (chats, notes, settings, keys). For people who downloaded the zip and never ran an installer.
static class Uninstall
{
    public static bool Run()
    {
        using var f = new Form
        {
            Text = "Uninstall Otto", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9.5f), Padding = new Padding(16), Icon = Icons.App(),
        };
        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        f.Controls.Add(grid);
        grid.Controls.Add(new Label { Text = "Remove Otto from this PC? It stops starting with Windows, and its shortcuts and program file are deleted.", AutoSize = true, MaximumSize = new Size(420, 0) });
        var everything = new CheckBox { Text = "Also delete my chats, notes, routines, settings and saved keys", AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        grid.Controls.Add(everything);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Size = new Size(420, 44), Margin = new Padding(0, 16, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Uninstall", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        grid.Controls.Add(buttons);
        f.AcceptButton = cancel; // Enter shouldn't uninstall by accident
        f.CancelButton = cancel;
        if (f.ShowDialog() != DialogResult.OK) return false;

        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            run?.DeleteValue("Otto", false);
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) })
            try { File.Delete(Path.Combine(dir, "Otto.lnk")); } catch { }

        if (everything.Checked)
        {
            Graph.SignOut();
            Imap.Disconnect();
            foreach (var p in Providers.All)
                foreach (var e in KeyRing.List(p)) KeyRing.Remove(p, e.Slot);
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Otto", false); } catch { }
            try { Directory.Delete(Paths.Data, true); } catch { }
        }

        // the running exe can't delete itself: a short-lived cmd waits for it to close, then removes it
        // (and its folder, when it's Otto's own install folder)
        if (Environment.ProcessPath is string exe)
        {
            var dir = Path.GetDirectoryName(exe)!;
            bool ownFolder = Path.GetFileName(dir).Equals("Otto", StringComparison.OrdinalIgnoreCase)
                             && Directory.GetFiles(dir).All(x => Path.GetFileName(x).StartsWith("Otto", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var target = ownFolder ? $"rmdir /s /q \"{dir}\"" : $"del /f /q \"{exe}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & {target}")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        MessageBox.Show(everything.Checked ? "Otto and everything it saved have been removed." : "Otto has been removed. Your chats and settings are kept in case you reinstall.",
            "Otto", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }
}
