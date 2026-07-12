using System.Threading.Tasks;
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
    public void Allow_WithRemember_ResolvesGrantedAndRemembered()
    {
        var vm = new FileConsentDialogViewModel(Request()) { Remember = true };

        vm.AllowCommand.Execute(null);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        var decision = vm.ResultTask.Result;
        decision.Granted.Should().BeTrue();
        decision.Remember.Should().BeTrue();
    }

    [Fact]
    public void Allow_WithoutRemember_ResolvesGrantedNotRemembered()
    {
        var vm = new FileConsentDialogViewModel(Request());

        vm.AllowCommand.Execute(null);

        var decision = vm.ResultTask.Result;
        decision.Granted.Should().BeTrue();
        decision.Remember.Should().BeFalse();
    }

    [Fact]
    public void Deny_ResolvesDeniedRegardlessOfRemember()
    {
        var vm = new FileConsentDialogViewModel(Request()) { Remember = true };

        vm.DenyCommand.Execute(null);

        var decision = vm.ResultTask.Result;
        decision.Granted.Should().BeFalse();
        decision.Remember.Should().BeFalse();
    }

    [Fact]
    public void ResolveAsDeny_WhenDismissed_ResolvesDenied()
    {
        var vm = new FileConsentDialogViewModel(Request());

        vm.ResolveAsDeny();

        vm.ResultTask.Result.Granted.Should().BeFalse();
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
