using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Renaming a paired device from the Settings card (RemEx-4gbp2).
/// </summary>
/// <remarks>
/// <para>
/// THE JOIN IS TESTED FIRST HERE, on purpose. Four times this session a bead landed a fully tested
/// component that nothing called, and every one of them hid a defect at the join that no amount of
/// testing the pure half could reach. <see cref="PairedDeviceDisplayName"/> already has ten tests
/// for the naming rules; none of them says a rename ever reaches a store.
/// </para>
/// <para>
/// Renaming is DISPLAY ONLY and must never touch the pairing registry. That is enforced structurally
/// — the writer holds the name store and nothing else — and asserted here so the structure cannot be
/// quietly widened.
/// </para>
/// </remarks>
public class PairedDeviceRenameTests
{
    [Fact]
    public void TypingANameAndApplyingItReachesTheWriter()
    {
        // THE JOIN. A command that resolves nothing, or reads the wrong property, produces a button
        // that appears to work and changes nothing — and Avalonia would report neither.
        var writer = new RecordingWriter();
        using var _ = new ScopedPairedDeviceServices(new FakeSource([Row("phone-a")]), writer);

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();
        vm.PairedDevices.Should().HaveCount(1);
        vm.PairedDevices[0].PendingName = "Study Pixel";

        vm.ApplyPairedDeviceRenameCommand.Execute(vm.PairedDevices[0]);

        writer.Calls.Should().ContainSingle();
        writer.Calls[0].Should().Be(("phone-a", "Study Pixel"));
    }

    [Fact]
    public void ApplyingABlankNameIsPassedThroughSoTheStoreCanClearIt()
    {
        // BLANK CLEARS THE OVERRIDE, and the view model must not swallow it as "nothing to do".
        // Dropping it here would leave the user unable to undo a rename: the field they would use to
        // fix it is the one they just emptied.
        var writer = new RecordingWriter();
        using var _ = new ScopedPairedDeviceServices(new FakeSource([Row("phone-a", "Old Name")]), writer);

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();
        vm.PairedDevices[0].PendingName = "   ";

        vm.ApplyPairedDeviceRenameCommand.Execute(vm.PairedDevices[0]);

        writer.Calls.Should().ContainSingle();
        writer.Calls[0].ClientId.Should().Be("phone-a");
        writer.Calls[0].TypedName.Should().Be("   ", "normalization is the store's rule, not the UI's");
    }

    [Fact]
    public void TheRowIsRebuiltFromTheStoreRatherThanFromWhatWasTyped()
    {
        // The store trims and caps at 48 characters, so what was typed and what is now stored are not
        // always the same string. Echoing the typed text would tell the user a 60-character name had
        // been kept whole.
        var source = new FakeSource([Row("phone-a")]);
        using var _ = new ScopedPairedDeviceServices(source, new RecordingWriter());

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();
        vm.PairedDevices[0].PendingName = "Whatever The User Typed";

        // The host is the authority: it reports a different name than was typed.
        source.Rows = [Row("phone-a", "What The Store Kept")];
        vm.ApplyPairedDeviceRenameCommand.Execute(vm.PairedDevices[0]);

        vm.PairedDevices[0].DisplayName.Should().Be("What The Store Kept");
    }

    [Fact]
    public void WithNoWriterTheRenameControlsAreNotOffered()
    {
        using var _ = new ScopedPairedDeviceServices(new FakeSource([Row("phone-a")]), writer: null);

        NewSettingsViewModel().CanRenamePairedDevices.Should().BeFalse(
            "a rename field that cannot save is worse than none — the user types, presses the button "
            + "and nothing happens, with nothing to explain it");
    }

    [Fact]
    public void ApplyingWithNoWriterDoesNotThrow()
    {
        // The command is bound whether or not the seam resolved; the guard has to be in the command,
        // not only in the visibility.
        using var _ = new ScopedPairedDeviceServices(new FakeSource([Row("phone-a")]), writer: null);

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();

        var apply = () => vm.ApplyPairedDeviceRenameCommand.Execute(vm.PairedDevices.FirstOrDefault());
        apply.Should().NotThrow();
    }

    [Fact]
    public void TheRenameControlsAreBoundInTheRowTemplate()
    {
        // Avalonia binding failures are silent, and the count floor is what stops this going quiet if
        // the row template is renamed — the failure mode that caught every guard I wrote this session.
        var flattened = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml")),
            @"\s+", " ");

        var template = Regex.Match(
            flattened, "<DataTemplate x:DataType=\"vm:PairedDeviceItem\">.*?</DataTemplate>",
            RegexOptions.Singleline);
        template.Success.Should().BeTrue("the paired-device row template must exist to be checked");

        template.Value.Should().Contain("{Binding PendingName}",
            "the field must write to the pending name, not to the label the row shows");
        template.Value.Should().Contain("ApplyPairedDeviceRenameCommand",
            "the button must reach the command, or it is decoration");
        template.Value.Should().Contain("CommandParameter=\"{Binding}\"",
            "without the row as the parameter the command has no device to rename");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static PairedDeviceRow Row(string id, string? name = null)
        => new(id, DeviceName: null, NameOverride: name, null, null, IsOnline: false);

    private static SettingsViewModel NewSettingsViewModel()
    {
        var savefile = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        return new SettingsViewModel(null!, new ConnectionViewModel(), null!, null!, savefile);
    }

    private sealed class FakeSource(IReadOnlyList<PairedDeviceRow> rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> Rows { get; set; } = rows;
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => Rows;
    }

    private sealed class RecordingWriter : IPairedDeviceNameWriter
    {
        public List<(string ClientId, string? TypedName)> Calls { get; } = [];
        public void Rename(string clientId, string? typedName) => Calls.Add((clientId, typedName));
    }

    /// <summary>Installs both paired-device services for one test, and restores what was there.</summary>
    private sealed class ScopedPairedDeviceServices : IDisposable
    {
        private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

        public ScopedPairedDeviceServices(IPairedDeviceSource source, IPairedDeviceNameWriter? writer)
            => App.EmbeddedHostServices = new TwoServices(source, writer);

        public void Dispose() => App.EmbeddedHostServices = _saved;

        private sealed class TwoServices(IPairedDeviceSource source, IPairedDeviceNameWriter? writer)
            : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IPairedDeviceSource)) return source;
                if (serviceType == typeof(IPairedDeviceNameWriter)) return writer;
                return null;
            }
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
