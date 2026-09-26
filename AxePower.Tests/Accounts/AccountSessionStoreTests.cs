using System;
using System.IO;
using System.Text;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;

namespace AxePower.Tests.Accounts;

/// <summary>The saved sign-in, with real DPAPI, in a temp folder (never the user's AppData).</summary>
public sealed class AccountSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerAccountTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static StoredAccountSession Sample() => new()
    {
        AccessToken = "access-secret-123",
        RefreshToken = "refresh-secret-456",
        ExpiresAtUtc = new DateTime(2026, 9, 26, 6, 0, 0, DateTimeKind.Utc),
        UserId = "u-1",
        Email = "me@example.com",
        Provider = "email",
        Profile = new AccountProfile("u-1", "mrabhimanyu", "Abhimanyu", null, new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc), null)
    };

    [Fact]
    public void Load_WithNothingSaved_ReturnsNull_AndCreatesNothing()
    {
        var store = new AccountSessionStore(new LogService(), _dir);

        Assert.Null(store.Load());
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndTheFileIsEncrypted()
    {
        var store = new AccountSessionStore(new LogService(), _dir);
        store.Save(Sample());

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("refresh-secret-456", loaded!.RefreshToken);
        Assert.Equal("mrabhimanyu", loaded.Profile!.Username);
        Assert.Equal(Sample().ExpiresAtUtc, loaded.ExpiresAtUtc);

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath));
        Assert.DoesNotContain("refresh-secret-456", raw);
        Assert.DoesNotContain("access-secret-123", raw);
        Assert.DoesNotContain("mrabhimanyu", raw);
    }

    [Fact]
    public void ACorruptedFile_ReadsAsSignedOut()
    {
        Directory.CreateDirectory(_dir);
        var store = new AccountSessionStore(new LogService(), _dir);
        File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3, 4 });

        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_RemovesTheFile()
    {
        var store = new AccountSessionStore(new LogService(), _dir);
        store.Save(Sample());

        store.Clear();

        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Load());
    }
}
