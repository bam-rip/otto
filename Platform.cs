using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;

namespace Otto;

/// API keys live in Windows Credential Manager (DPAPI-encrypted by Windows): "Otto" for Claude,
/// "Otto:<provider>" for the others.
static class KeyStore
{

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr p);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    public static void Delete(string target) => CredDelete(target, 1, 0);

    public static string? ApiKey(string target, string? envFallback)
    {
        var env = envFallback == null ? null : Environment.GetEnvironmentVariable(envFallback);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (!CredRead(target, 1, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2).Trim();
        }
        finally { CredFree(ptr); }
    }

    public static void Save(string key, string target)
    {
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var c = new CREDENTIAL
            {
                Type = 1, // generic
                TargetName = target,
                UserName = "anthropic",
                CredentialBlob = blob,
                CredentialBlobSize = key.Length * 2,
                Persist = 2, // local machine
            };
            if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception();
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
}

/// The app used to be called Jarvis. On first start as Otto, carry everything over so nothing is lost:
/// notes, routines, sounds and backups (folder), settings (registry), API keys, and start-with-Windows.
static class Migration
{
    public static void FromJarvis()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            string old = Path.Combine(local, "Jarvis"), neu = Path.Combine(local, "Otto");
            if (Directory.Exists(old) && !Directory.Exists(neu)) Directory.Move(old, neu);
        }
        catch { }
        try { CopyKey(@"Software\Jarvis", @"Software\Otto"); } catch { }
        foreach (var p in Providers.All)
        {
            try
            {
                var oldTarget = p.IsAnthropic ? "Jarvis" : "Jarvis:" + p.Id;
                if (KeyStore.ApiKey(p.KeyTarget, null) == null && KeyStore.ApiKey(oldTarget, null) is string k)
                    KeyStore.Save(k, p.KeyTarget);
            }
            catch { }
        }
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("Jarvis") != null)
            {
                run.DeleteValue("Jarvis", false);
                run.SetValue("Otto", $"\"{Environment.ProcessPath}\"");
            }
        }
        catch { }
    }

    static void CopyKey(string from, string to)
    {
        using var src = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(from);
        if (src == null || Microsoft.Win32.Registry.CurrentUser.OpenSubKey(to) != null) return;
        using var dst = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(to);
        foreach (var name in src.GetValueNames()) dst.SetValue(name, src.GetValue(name)!, src.GetValueKind(name));
        foreach (var sub in src.GetSubKeyNames()) CopyKey(from + "\\" + sub, to + "\\" + sub);
    }
}

sealed class Hotkeys : NativeWindow, IDisposable
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4;
    const uint NoRepeat = 0x4000;
    const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    readonly List<int> ids = new();
    public event Action<int>? Pressed;

    public Hotkeys() => CreateHandle(new CreateParams());

    public bool Register(int id, uint mods, Keys key)
    {
        if (!RegisterHotKey(Handle, id, mods | NoRepeat, (uint)key)) return false;
        ids.Add(id);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in ids) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}

static class Icons
{
    /// White ring for the tray (assets/otto-tray.ico, embedded), sized for the taskbar's DPI.
    public static Icon Tray() => Load("otto-tray.ico", SystemInformation.SmallIconSize);

    /// The full-colour app icon, for windows.
    public static Icon App() => Load("otto.ico", SystemInformation.IconSize);

    static Icon Load(string name, Size size)
    {
        using var s = typeof(Icons).Assembly.GetManifestResourceStream(name)!;
        return new Icon(s, size);
    }
}
