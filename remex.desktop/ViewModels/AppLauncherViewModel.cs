using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Core.Messages;

namespace Remex.Desktop.ViewModels;

public partial class AppLauncherViewModel : ObservableObject, IDisposable
{
    private const string DefaultHexColor = "#4A3AFF";

    private readonly ShellViewModel _shell;
    private readonly ILauncherStorageService _storageService;
    private readonly RemexSavefileService? _savefileService;
    private readonly IIconExtractionService? _iconService;
    private readonly Action<System.Collections.Generic.List<AppEntry>> _launcherEntriesHandler;

    public ConnectionViewModel Connection { get; }

    /// <summary>Exposes ShellViewModel so the view's code-behind can gate the entrance animation on
    /// reduced motion (RemEx-alwfa.2), same pattern as HomeViewModel.Shell.</summary>
    public ShellViewModel Shell => _shell;

    [ObservableProperty]
    private ObservableCollection<AppEntry> _launchers = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    public IEnumerable<AppEntry> FilteredApps =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Launchers
            : Launchers.Where(a => a.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when apps exist but the current search matches none of them.</summary>
    /// <remarks>
    /// Deliberately NOT simply "FilteredApps is empty". That would fire when the launcher has no
    /// apps at all, doubling up with the existing <c>AppLauncher_NoApps</c> message and telling the
    /// user to refine a search they never made. The two states are distinct: nothing configured
    /// versus nothing matching. (RemEx-n69m.)
    /// </remarks>
    public bool ShowNoSearchResults => Launchers.Count > 0 && !FilteredApps.Any();

    private void NotifyFilterChanged()
    {
        OnPropertyChanged(nameof(FilteredApps));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    partial void OnSearchTextChanged(string value) => NotifyFilterChanged();

    partial void OnLaunchersChanged(ObservableCollection<AppEntry> value) => NotifyFilterChanged();

    public AppLauncherViewModel(ConnectionViewModel connection, ShellViewModel shell, ILauncherStorageService storageService, RemexSavefileService? savefileService = null, IIconExtractionService? iconService = null)
    {
        Connection = connection;
        _shell = shell;
        _storageService = storageService;
        // Optional: absent in tests and on any platform without an icon extractor registered, in
        // which case the stored icon is left exactly as it was found.
        _iconService = iconService;
        // Optional: only populated once WP-A's savefile service is registered in DI. Used solely
        // to nudge the rolling auto-snapshot after a local (unconnected) launcher save.
        _savefileService = savefileService;

        _launcherEntriesHandler = entries =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Launchers = new ObservableCollection<AppEntry>(NormalizeEntries(entries));
            });
        };
        Connection.LauncherEntriesReceived += _launcherEntriesHandler;

        _ = LoadLaunchersAsync();
    }

    private ConnectionViewModel _connection => Connection;

    private async Task LoadLaunchersAsync()
    {
        // If connected, host will sync. Fallback to local storage
        var entries = await _storageService.LoadEntriesAsync();
        var normalized = NormalizeEntries(entries).ToList();

        var upgraded = UpgradeLowResolutionIcons(normalized, out var changed);
        Launchers = new ObservableCollection<AppEntry>(upgraded);

        if (changed)
        {
            await SaveLaunchersAsync();
        }
    }

    /// <summary>
    /// Minimum stored icon edge, in pixels, that the 80px launcher tile can draw without visible
    /// upscaling. Mirrors <c>DesktopIconExtractionService.LowResolutionIconEdge</c>; duplicated as a
    /// literal because remex.desktop cannot reference the platform-specific agent assembly.
    /// </summary>
    private const int MinimumIconEdge = 64;

    /// <summary>
    /// Re-extracts any stored icon that is too small for the tile it is drawn in.
    /// </summary>
    /// <remarks>
    /// Every entry added before RemEx-u4244 carries a baked 32x32 PNG, because the old Windows
    /// extractor could not produce anything else. Fixing the extractor alone would leave those
    /// entries blurry forever — the savefile is the source of truth and nothing re-reads the
    /// executable. So the size is checked on load and a stale icon is refreshed in place. Entries
    /// whose target no longer exists, or whose re-extraction yields nothing better, keep the icon
    /// they have rather than losing it.
    /// </remarks>
    private IEnumerable<AppEntry> UpgradeLowResolutionIcons(List<AppEntry> entries, out bool changed)
    {
        changed = false;

        if (_iconService is null)
            return entries;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (string.IsNullOrWhiteSpace(entry.TargetPath) || !File.Exists(entry.TargetPath))
                continue;

            if (!NeedsSharperIcon(entry.IconBase64))
                continue;

            string? refreshed;
            try
            {
                refreshed = _iconService.ExtractIconAsBase64(entry.TargetPath);
            }
            catch
            {
                // A launcher entry is still usable with a soft icon; it is not usable if a throwing
                // extractor takes the whole page down on load.
                continue;
            }

            if (string.IsNullOrWhiteSpace(refreshed) || refreshed == entry.IconBase64)
                continue;

            if (NeedsSharperIcon(refreshed))
                continue;

            entries[i] = entry with { IconBase64 = refreshed };
            changed = true;
        }

        return entries;
    }

    /// <summary>
    /// Encoded bytes per pixel below which a stored icon is mostly empty canvas rather than artwork.
    /// </summary>
    /// <remarks>
    /// Measured against this machine's 50-entry launcher: the two parked-canvas icons came in at
    /// 0.010 and 0.011 bytes per pixel, while the leanest genuine 256px icon — a flat two-tone glyph —
    /// was 0.053. The threshold sits in that gap with roughly a factor of two either side, so it is
    /// not balanced on a knife edge.
    /// </remarks>
    private const double MinimumIconBytesPerPixel = 0.025;

    /// <summary>
    /// True when a stored icon is worth re-extracting: unreadable, too small, or mostly empty.
    /// </summary>
    /// <remarks>
    /// SIZE ALONE IS NOT ENOUGH, and assuming it was is what left 7-Zip and FastCopy looking wrong
    /// after the first pass. The shell hands back a full 256x256 bitmap for a file that has no 256px
    /// icon variant, with the small artwork parked in a corner and everything else transparent.
    /// Those entries measure 256 wide and sail past a dimension check, while rendering as a
    /// thumbnail-sized glyph in the corner of an otherwise empty tile.
    /// </remarks>
    internal static bool NeedsSharperIcon(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return true;

        if (TryGetPngEdge(base64) is not int edge)
            return true;

        if (edge < MinimumIconEdge)
            return true;

        // Base64 carries 3 bytes per 4 characters; exact enough for a density ratio.
        var encodedBytes = base64.Length / 4.0 * 3.0;
        return encodedBytes < edge * (double)edge * MinimumIconBytesPerPixel;
    }

    /// <summary>
    /// Reads the pixel width out of a base64 PNG's IHDR chunk, or null if it is not a decodable PNG.
    /// </summary>
    /// <remarks>
    /// Header-only on purpose. This runs over every entry on every launcher load, and fully decoding
    /// each bitmap just to read two numbers would put image decoding on the UI startup path.
    /// </remarks>
    internal static int? TryGetPngEdge(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        // 8-byte signature + 4-byte length + "IHDR" + 4-byte width + 4-byte height = 24 bytes.
        Span<byte> header = stackalloc byte[24];
        Span<char> chars = stackalloc char[32];
        var prefixLength = Math.Min(32, base64.Length);
        base64.AsSpan(0, prefixLength).CopyTo(chars);

        // Whole 4-char groups only: base64 decodes in quartets, and a partial group is not decodable.
        if (!System.Convert.TryFromBase64Chars(chars[..(prefixLength / 4 * 4)], header, out var written) || written < 24)
            return null;

        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(pngSignature))
            return null;

        if (!header.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));

        return width <= 0 || height <= 0 ? null : Math.Min(width, height);
    }

    public async Task SaveLaunchersAsync()
    {
        await _storageService.SaveEntriesAsync(Launchers);
        // Single hook point for every local (unconnected) launcher persist path — Remove, Submit
        // (Android add panel), PersistOrderAsync, and the add/edit dialogs all funnel through here.
        _savefileService?.NotifyStateChanged();
    }

    private static AppEntry NormalizeEntry(AppEntry entry)
    {
        var targetPath = NormalizeString(entry.TargetPath);
        var displayName = NormalizeString(entry.DisplayName);
        var hexColor = NormalizeString(entry.HexColor);
        var iconBase64 = NormalizeString(entry.IconBase64);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                displayName = Path.GetFileNameWithoutExtension(targetPath);
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = LocalizationService.Instance["AppLauncher_UnnamedApp"];
            }
        }

        if (string.IsNullOrWhiteSpace(hexColor) || !hexColor.StartsWith("#", StringComparison.Ordinal))
        {
            hexColor = DefaultHexColor;
        }

        if (string.IsNullOrWhiteSpace(iconBase64))
        {
            iconBase64 = null;
        }

        return new AppEntry(
            entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            displayName,
            targetPath,
            hexColor,
            iconBase64,
            entry.Order);
    }

    private static string NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static System.Collections.Generic.List<AppEntry> NormalizeEntries(System.Collections.Generic.IEnumerable<AppEntry> entries)
    {
        return entries
            .Select(NormalizeEntry)
            .GroupBy(e => string.IsNullOrWhiteSpace(e.TargetPath) ? e.Id.ToString() : e.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Order)
            .ToList();
    }

    [RelayCommand]
    private async Task LaunchAppAsync(AppEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.TargetPath))
            return;

        // Feed the Home "Recent activity" panel. The display name is the useful bit regardless of
        // whether the launch is forwarded to a connected phone or run on this PC below.
        ActivityService.Instance.Record(ActivityKind.AppLaunched, entry.DisplayName);

        if (Connection.IsConnected)
        {
            var p = new System.Collections.Generic.Dictionary<string, string> { { "TargetPath", entry.TargetPath } };
            await Connection.SendCommandAsync("LaunchApp", p);
        }
        else
        {
            // Not connected to a remote phone: launch on THIS PC via the in-process host's launcher.
            // (Formerly forwarded over the RemExLocalIPC pipe to a separate service process.) (RemEx-aep Phase 3)
            await Remex.Desktop.Services.EmbeddedHostServiceLocator
                .Require<Remex.Core.Services.IAppLauncherService>()
                .LaunchAppAsync(entry.TargetPath);
        }
    }

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    [RelayCommand]
    private async Task RemoveAppAsync(AppEntry entry)
    {
        if (entry != null && Launchers.Contains(entry))
        {
            // The card and its custom colour/icon are gone once removed, so confirm first
            // (RemEx-6p1f). Fails CLOSED: with no dialog wired the card stays.
            // Uses its own Btn key rather than AppLauncher_Remove, which is the ✕ button's TOOLTIP
            // (AppLauncherView.axaml:110) — rewording a tooltip must not silently reword a dialog.
            if (OnConfirmationRequested is null
                || !await OnConfirmationRequested(
                    LocalizationService.Instance["Confirm_RemoveApp_Title"],
                    string.Format(
                        LocalizationService.Instance["Confirm_RemoveApp_Format"],
                        entry.DisplayName),
                    LocalizationService.Instance["Confirm_RemoveApp_Btn"]))
            {
                return;
            }

            Launchers.Remove(entry);

            if (Connection.IsConnected)
            {
                var msg = new RemexMessage { Type = MessageTypes.LauncherRemove, LauncherEntry = entry };
                await Connection.SendAsync(msg);
            }
            else
            {
                await SaveLaunchersAsync();
            }
        }
    }

    [RelayCommand]
    private void NavigateBack() => _shell.NavigateToHome();

    public Action<AppEntry>? OnOpenEditProgramDialogRequested { get; set; }

    [RelayCommand]
    private void EditApp(AppEntry entry) => OnOpenEditProgramDialogRequested?.Invoke(entry);

    /// <summary>
    /// Applies an edited entry in place (replace-in-place keeps the card's visual slot), then
    /// persists — <see cref="PersistOrderAsync"/> sends a <c>LauncherSync</c> when connected, else
    /// saves to disk.
    /// </summary>
    public async Task ApplyEditedEntryAsync(AppEntry updated)
    {
        var idx = -1;
        for (var i = 0; i < Launchers.Count; i++)
        {
            if (Launchers[i].Id == updated.Id)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0) return;

        Launchers[idx] = updated;
        NotifyFilterChanged();
        await PersistOrderAsync();
    }

    /// <summary>
    /// One-shot sort by display name — operates on the full <see cref="Launchers"/> collection
    /// regardless of any active search filter. No sticky sort mode: a later drag or arrow move
    /// persists the new manual order on top of this.
    /// </summary>
    [RelayCommand]
    private async Task SortByNameAsync(string direction)
    {
        var ordered = LauncherOrdering.SortByName(Launchers, direction);
        Launchers = new ObservableCollection<AppEntry>(ordered);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private void OpenAddProgramDialog()
    {
        // The Android in-place add panel was unreachable in this assembly and was removed with
        // the rest of the dead Android chrome (RemEx-f167), along with its backing properties
        // and SubmitAndroidNewAppAsync. The desktop dialog is the only path.
        OnOpenAddProgramDialogRequested?.Invoke();
    }

    /// <summary>
    /// Adds launcher entries for files (e.g. .lnk / .exe shortcuts) dropped onto the launcher grid.
    /// Mirrors the "Add program" dialog's save path: when connected the entry is sent to the host,
    /// otherwise it is added and persisted locally.
    /// </summary>
    public async Task AddDroppedAppsAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) return;

        var addedLocally = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileName(path);

            var entry = NormalizeEntry(new AppEntry(Guid.NewGuid(), name, path, "#4A3AFF", null));

            if (Connection.IsConnected)
            {
                var msg = new RemexMessage { Type = MessageTypes.LauncherAdd, LauncherEntry = entry };
                await Connection.SendAsync(msg);
            }
            else
            {
                Launchers.Add(entry);
                addedLocally = true;
            }
        }

        if (addedLocally)
            await SaveLaunchersAsync();
    }

    [RelayCommand]
    private async Task ReorderLauncherAsync((AppEntry source, AppEntry target) param)
    {
        var sourceIndex = Launchers.IndexOf(param.source);
        var targetIndex = Launchers.IndexOf(param.target);

        if (sourceIndex == -1 || targetIndex == -1 || sourceIndex == targetIndex)
            return;

        Launchers.Move(sourceIndex, targetIndex);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private async Task MoveLeftAsync(AppEntry entry)
    {
        if (entry == null) return;
        var index = Launchers.IndexOf(entry);
        if (index <= 0) return;
        Launchers.Move(index, index - 1);
        await PersistOrderAsync();
    }

    [RelayCommand]
    private async Task MoveRightAsync(AppEntry entry)
    {
        if (entry == null) return;
        var index = Launchers.IndexOf(entry);
        if (index < 0 || index >= Launchers.Count - 1) return;
        Launchers.Move(index, index + 1);
        await PersistOrderAsync();
    }

    private async Task PersistOrderAsync()
    {
        // Update Order property for all
        var reindexed = LauncherOrdering.Reindex(Launchers);
        for (int i = 0; i < reindexed.Count; i++)
        {
            Launchers[i] = reindexed[i];
        }

        if (Connection.IsConnected)
        {
            var msg = new RemexMessage { Type = MessageTypes.LauncherSync, LauncherEntries = Launchers.ToList() };
            await Connection.SendAsync(msg);
        }
        else
        {
            await SaveLaunchersAsync();
        }
    }

    public Action? OnOpenAddProgramDialogRequested { get; set; }

    public void Dispose()
    {
        Connection.LauncherEntriesReceived -= _launcherEntriesHandler;
    }
}
