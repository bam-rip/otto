using Microsoft.Win32;

namespace Otto.Tests;

/// Uses a made-up provider id so it never touches real keys, and removes everything it wrote.
public class KeyRingTests : IDisposable
{
    readonly Provider p = new("test-keyring-" + Guid.NewGuid().ToString("N")[..8], "Test", "", "m", "m", false, "");

    [Fact]
    public void Keys_can_be_added_switched_and_removed()
    {
        Assert.Empty(KeyRing.List(p));
        Assert.Null(p.SavedKey());

        int a = KeyRing.Add(p, "Personal", "key-aaaa-1111");
        int b = KeyRing.Add(p, "Work", "key-bbbb-2222");
        Assert.Equal(0, a); // first key uses the original credential entry
        Assert.Equal(new[] { "Personal", "Work" }, KeyRing.List(p).Select(e => e.Label));
        Assert.Equal("…1111", KeyRing.List(p)[0].Hint);
        Assert.Equal("key-aaaa-1111", p.SavedKey()); // the first key added is active

        Assert.True(KeyRing.Next(p, out var next));
        Assert.Equal("Work", next!.Label);
        Assert.Equal("key-bbbb-2222", p.SavedKey());
        Assert.True(KeyRing.Next(p, out next)); // wraps round
        Assert.Equal("Personal", next!.Label);

        KeyRing.SetActive(p, b);
        KeyRing.Remove(p, b);
        Assert.Equal("key-aaaa-1111", p.SavedKey()); // removing the active key falls back to another
        Assert.False(KeyRing.Next(p, out _)); // only one left: nothing to switch to
    }

    public void Dispose()
    {
        for (int slot = 0; slot < 5; slot++) KeyStore.Delete(p.KeyTargetFor(slot));
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Otto\AI", writable: true);
        if (k == null) return;
        foreach (var name in k.GetValueNames().Where(n => n.StartsWith(p.Id + ".")))
            k.DeleteValue(name);
    }
}
