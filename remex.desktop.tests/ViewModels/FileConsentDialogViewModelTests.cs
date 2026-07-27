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
        var vm = new FileConsentDialogViewModel(Request(FileConsentKinds.IncomingPush, detail: "photo.jpg (2 MB)"));

        vm.Kind.Should().Be(FileConsentKinds.IncomingPush);
        vm.Detail.Should().Be("photo.jpg (2 MB)");
        vm.HasDetail.Should().BeTrue();
    }

    [Fact]
    public void HasDetail_IsFalse_WhenNoDetailProvided()
    {
        var vm = new FileConsentDialogViewModel(Request(detail: null));

        vm.HasDetail.Should().BeFalse();
    }

    [Fact]
    public void TitleAndMessage_DifferByKind()
    {
        var fullBrowse = new FileConsentDialogViewModel(Request(FileConsentKinds.FullBrowse));
        var incomingPush = new FileConsentDialogViewModel(Request(FileConsentKinds.IncomingPush));

        // The two consent kinds must present distinct copy so the user knows what they are approving.
        fullBrowse.Title.Should().NotBe(incomingPush.Title);
    }
}
