namespace Otto;

/// On/off preferences, as DWORDs in HKCU\Software\Otto (value names kept from earlier versions).
static class Prefs
{
    const string Key = @"Software\Otto";

    public static bool AnimateReplies { get => Get("AnimateReplies", true); set => Set("AnimateReplies", value); }
    public static bool VoiceAutoSend { get => Get("VoiceAutoSend", false); set => Set("VoiceAutoSend", value); }
    public static bool ReadAloud { get => Get("ReadAloud", false); set => Set("ReadAloud", value); }
    public static bool Sounds { get => Get("Sounds", true); set => Set("Sounds", value); }
    public static bool CheckUpdates { get => Get("CheckUpdates", true); set => Set("CheckUpdates", value); }
    public static bool Welcomed { get => Get("Welcomed", false); set => Set("Welcomed", value); }

    static bool Get(string name, bool fallback) => Reg.GetInt(Key, name) is int v ? v != 0 : fallback;
    static void Set(string name, bool on) => Reg.SetInt(Key, name, on ? 1 : 0);
}
