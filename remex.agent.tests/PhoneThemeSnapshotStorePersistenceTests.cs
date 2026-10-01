using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Theme;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// "Match phone" must survive a PC restart (RemEx-qean1): the host's store persists the last
/// accepted snapshot and a fresh instance over the same directory loads it back. A missing or
/// corrupt file means "no snapshot", never an exception. Every test runs in its own temp
/// directory and never touches the real per-user data folder.
/// </summary>
public sealed class PhoneThemeSnapshotStorePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "remex-phone-theme-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, PhoneThemeSnapshotStore.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private PhoneThemeSnapshotStore NewStore() =>
        new(NullLogger<PhoneThemeSnapshotStore>.Instance, StorePath);

    private static PhoneThemeSnapshot Valid() => new()
    {
        SeedHex = "#6750A4",
        Style = "tonal_spot",
        Mode = "dark",
        Contrast = 0.5,
        DynamicColor = true,
        SentAtUnixMs = 1_700_000_000_000,
        ClientId = "phone-1",
        ReceivedUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Set_ThenNewInstanceOverSameDirectory_LoadsTheSameSnapshot()
    {
        var snapshot = Valid();
        NewStore().Set(snapshot);

        var reloaded = NewStore().Latest;

        Assert.NotNull(reloaded);
        Assert.Equal(snapshot, reloaded);
    }

    [Fact]
    public void MissingFile_LoadsAsNull()
    {
        Assert.Null(NewStore().Latest);
    }

    [Fact]
    public void CorruptFile_LoadsAsNullWithoutThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ this is not json");

        var store = NewStore();

        Assert.Null(store.Latest);
    }

    [Fact]
    public void FileThatFailsWireValidation_LoadsAsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, """{"seed":"not-a-colour","style":"tonal_spot","mode":"dark","contrast":0}""");

        Assert.Null(NewStore().Latest);
    }

    [Fact]
    public void CorruptFile_IsReplacedByTheNextSet()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "garbage");
        var snapshot = Valid();

        NewStore().Set(snapshot);

        Assert.Equal(snapshot, NewStore().Latest);
    }
}
