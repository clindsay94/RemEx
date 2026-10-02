using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class FileConsentDialogViewModelTests
{
    private static FileConsentRequest Request(string kind = FileConsentKinds.FullBrowse, string? detail = null) =>
        new() { ConsentId = "consent-1", Kind = kind, Detail = detail };

    [Fact]
    public async Task Allow_WithRemember_ResolvesGrantedAndRemembered()
    {
        var vm = new FileConsentDialogViewModel(Request()) { Remember = true };

        vm.AllowCommand.Execute(null);

        // Assert completion BEFORE awaiting. These commands resolve the decision synchronously,
        // and awaiting a task that never completed would hang the test until xUnit's timeout
        // rather than failing it with a useful message.
        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        var decision = await vm.ResultTask;
        decision.Granted.Should().BeTrue();
        decision.Remember.Should().BeTrue();
    }

    [Fact]
    public async Task Allow_WithoutRemember_ResolvesGrantedNotRemembered()
    {
        var vm = new FileConsentDialogViewModel(Request());

        vm.AllowCommand.Execute(null);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        var decision = await vm.ResultTask;
        decision.Granted.Should().BeTrue();
        decision.Remember.Should().BeFalse();
    }

    [Fact]
    public async Task Deny_ResolvesDeniedRegardlessOfRemember()
    {
        var vm = new FileConsentDialogViewModel(Request()) { Remember = true };

        vm.DenyCommand.Execute(null);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        var decision = await vm.ResultTask;
        decision.Granted.Should().BeFalse();
        decision.Remember.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsDeny_WhenDismissed_ResolvesDenied()
    {
        var vm = new FileConsentDialogViewModel(Request());

        vm.ResolveAsDeny();

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        var decision = await vm.ResultTask;
        decision.Granted.Should().BeFalse();
    }

    [Fact]
    public void Kind_And_Detail_ReflectRequest()
    {
        var vm = new FileConsentDialogViewModel(Request(FileConsentKinds.FullBrowse, detail: "Browse all drives"));

        vm.Kind.Should().Be(FileConsentKinds.FullBrowse);
        vm.Detail.Should().Be("Browse all drives");
        vm.HasDetail.Should().BeTrue();
    }

    [Fact]
    public void HasDetail_IsFalse_WhenNoDetailProvided()
    {
        var vm = new FileConsentDialogViewModel(Request(detail: null));

        vm.HasDetail.Should().BeFalse();
    }

    [Fact]
    public void TitleAndMessage_AreTheFullBrowseCopy()
    {
        var vm = new FileConsentDialogViewModel(Request(FileConsentKinds.FullBrowse));

        vm.Title.Should().Be(Remex.Desktop.Services.LocalizationService.Instance["FileConsent_FullBrowseTitle"]);
        vm.Message.Should().Be(Remex.Desktop.Services.LocalizationService.Instance["FileConsent_FullBrowseMessage"]);
    }

    /// <summary>
    /// The PC's incoming-push consent surface stays gone (RemEx-bezf).
    /// </summary>
    /// <remarks>
    /// RemEx-e11w deleted the PC's incoming-push prompt, which left a per-device "auto-accept incoming"
    /// switch that persisted a flag nothing read, plus a dialog branch and two strings for a prompt the
    /// PC never raises. A switch that does nothing still reads like protection, so all of it went.
    /// Each assertion below fails if its piece comes back.
    /// </remarks>
    [Fact]
    public void TheInertAutoAcceptIncomingSurfaceIsGone()
    {
        typeof(FileTrustDeviceItem).GetProperty("AutoAcceptIncoming").Should().BeNull(
            "the Settings switch bound to this, and nothing reads what it saved");

        var root = RepoRoot();
        var settingsView = File.ReadAllText(Path.Combine(root, "remex.desktop", "Views", "SettingsView.axaml"));
        settingsView.Should().NotContain("AutoAcceptIncoming").And.NotContain("Settings_TrustAutoAccept");

        var dialogVm = File.ReadAllText(Path.Combine(root, "remex.desktop", "ViewModels", "FileConsentDialogViewModel.cs"));
        dialogVm.Should().NotContain("FileConsent_IncomingPush",
            "the PC never asks about incoming pushes, so the dialog has no copy for them");

        var resx = Directory.GetFiles(Path.Combine(root, "remex.desktop", "Localization"), "Strings*.resx");
        resx.Should().HaveCount(9, "anti-vacuity: the scan must actually see all nine locale files");
        foreach (var file in resx)
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("\"Settings_TrustAutoAccept\"", Path.GetFileName(file));
            text.Should().NotContain("\"FileConsent_IncomingPushTitle\"", Path.GetFileName(file));
            text.Should().NotContain("\"FileConsent_IncomingPushMessage\"", Path.GetFileName(file));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (Remex.sln).");
    }
}
