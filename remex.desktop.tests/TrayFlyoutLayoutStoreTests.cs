using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

public class TrayFlyoutLayoutStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TrayFlyoutLayoutStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remex-tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "tray_flyout_layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TrayFlyoutLayoutStore Store() => new(_path);

    [Fact]
    public async Task Missing_file_loads_as_null()
    {
        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Saved_geometry_round_trips()
    {
        var saved = new TrayFlyoutGeometry { IsPinned = true, X = 120, Y = 340, Width = 460, Height = 380 };
        await Store().SaveAsync(saved);

        var loaded = await Store().LoadRawAsync();

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public async Task Corrupt_json_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, "{ this is not json");

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Empty_file_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, string.Empty);

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Json_null_literal_loads_as_null_without_throwing()
    {
        await File.WriteAllTextAsync(_path, "null");

        Assert.Null(await Store().LoadRawAsync());
    }

    [Fact]
    public async Task Save_overwrites_a_previous_value()
    {
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = true, X = 1, Y = 2, Width = 400, Height = 300 });
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = false, X = 9, Y = 8, Width = 500, Height = 350 });

        var loaded = await Store().LoadRawAsync();

        Assert.NotNull(loaded);
        Assert.False(loaded!.IsPinned);
        Assert.Equal(9, loaded.X);
    }

    [Fact]
    public async Task Save_to_an_unwritable_path_does_not_throw()
    {
        // The only caller is a DispatcherTimer tick, which is an async void handler by necessity -
        // an exception escaping it is raised on the synchronization context and takes the process
        // down (RemEx-ajk3). WriteAllTextAtomicAsync deletes its staging file and RETHROWS, so
        // without a catch here a locked file or a full volume closes RemEx mid-drag. Losing a
        // remembered window position is acceptable; losing the application is not.
        var store = new TrayFlyoutLayoutStore(Path.Combine(_dir, "no-such-folder", "layout.json"));

        var exception = await Record.ExceptionAsync(
            () => store.SaveAsync(new TrayFlyoutGeometry { IsPinned = true, X = 1, Y = 1, Width = 400, Height = 300 }));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Unpinned_state_survives_a_round_trip()
    {
        // IsPinned = false is the DEFAULT for a bool, so a serializer misconfiguration that drops
        // the property would still pass a pinned-only test. This is the one that catches it.
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = false, X = 5, Y = 5, Width = 400, Height = 300 });
        await Store().SaveAsync(new TrayFlyoutGeometry { IsPinned = true, X = 5, Y = 5, Width = 400, Height = 300 });

        var loaded = await Store().LoadRawAsync();

        Assert.True(loaded!.IsPinned);
    }
}
