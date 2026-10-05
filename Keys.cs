namespace Otto;

/// Several saved keys per provider (say a personal and a work key, or two free-tier keys). The keys live in
/// Credential Manager; which slots exist, their names, and the active one live in the registry.
static class KeyRing
{
    public sealed record Entry(int Slot, string Label, string Hint);

    public static List<Entry> List(Provider p)
    {
        var list = new List<Entry>();
        foreach (var slot in Slots(p))
        {
            var key = KeyStore.ApiKey(p.KeyTargetFor(slot), null);
            if (key == null) continue; // removed outside Otto
            list.Add(new(slot, Providers.Get(p, $"keylabel.{slot}", $"Key {slot + 1}"), Mask(key)));
        }
        return list;
    }

    /// "…a1b2": enough to tell keys apart without showing them.
    public static string Mask(string key) => key.Length <= 8 ? "…" : "…" + key[^4..];

    static IEnumerable<int> Slots(Provider p)
    {
        foreach (var part in Providers.Get(p, "keyslots", "0").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out var n)) yield return n;
    }

    static void SetSlots(Provider p, IEnumerable<int> slots) => Providers.Set(p, "keyslots", string.Join(",", slots));

    public static int Active(Provider p) => int.TryParse(Providers.Get(p, "activekey", "0"), out var n) ? n : 0;
    public static void SetActive(Provider p, int slot) => Providers.Set(p, "activekey", slot.ToString());

    public static int Add(Provider p, string label, string key)
    {
        var slots = Slots(p).ToList();
        // slot 0 is empty on a fresh install; fill it first so single-key setups stay as before
        int slot = KeyStore.ApiKey(p.KeyTarget, null) == null ? 0 : Enumerable.Range(1, 99).First(n => !slots.Contains(n));
        KeyStore.Save(key, p.KeyTargetFor(slot));
        if (!slots.Contains(slot)) slots.Add(slot);
        SetSlots(p, slots);
        Providers.Set(p, $"keylabel.{slot}", label.Length > 0 ? label : $"Key {slot + 1}");
        if (List(p).Count == 1) SetActive(p, slot);
        return slot;
    }

    public static void Remove(Provider p, int slot)
    {
        KeyStore.Delete(p.KeyTargetFor(slot));
        SetSlots(p, Slots(p).Where(s => s != slot));
        if (Active(p) == slot && List(p).FirstOrDefault() is Entry next) SetActive(p, next.Slot);
    }

    /// Move to the saved key after the active one. False when there's no other key.
    public static bool Next(Provider p, out Entry? now)
    {
        var list = List(p);
        now = null;
        if (list.Count < 2) return false;
        int i = list.FindIndex(e => e.Slot == Active(p));
        now = list[(i + 1) % list.Count];
        SetActive(p, now.Slot);
        return true;
    }
}

/// Small dialog for adding a key: a name to tell it apart, and the key itself.
static class KeyPrompt
{
    public static (string Label, string Key)? Ask(IWin32Window owner, string providerLabel, int number)
    {
        using var f = Ui.Dialog("Add a key for " + providerLabel, out var body);
        body.Controls.Add(Ui.Caption("Name (so you can tell your keys apart)"));
        var name = Ui.TextBox(420, $"Key {number}");
        body.Controls.Add(name);
        body.Controls.Add(Ui.Caption("API key"));
        var key = Ui.TextBox(420, password: true, placeholder: "Paste your key");
        body.Controls.Add(key);
        var ok = Ui.DialogButtons(f, body, "Add");
        ok.Enabled = false;
        key.TextChanged += (_, _) => ok.Enabled = key.Text.Trim().Length > 0;
        f.Shown += (_, _) => key.Focus();
        if (f.ShowDialog(owner) != DialogResult.OK || key.Text.Trim().Length == 0) return null;
        return (name.Text.Trim(), key.Text.Trim());
    }
}
