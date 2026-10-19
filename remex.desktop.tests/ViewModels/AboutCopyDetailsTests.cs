using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>About's "Copy details" button: the text it puts on the clipboard for a bug report.</summary>
public sealed class AboutCopyDetailsTests : IDisposable
{
    private readonly List<(NotificationImportance Importance, string Title, string Message)> _announced = [];
    private readonly IInAppNotificationSink? _savedInApp = NotificationService.Instance.InApp;
    private readonly Func<bool>? _savedProbe = NotificationService.Instance.WindowVisibleProbe;
    private readonly Action<Action> _savedDispatch = NotificationService.Instance.Dispatch;
    private readonly List<AboutViewModel> _created = [];

    public AboutCopyDetailsTests()
    {
        NotificationService.Instance.Dispatch = static work => work();
        NotificationService.Instance.WindowVisibleProbe = () => true;
        NotificationService.Instance.InApp = new RecordingSink(_announced);
    }

    public void Dispose()
    {
        foreach (var about in _created)
            about.Dispose();
        NotificationService.Instance.InApp = _savedInApp;
        NotificationService.Instance.WindowVisibleProbe = _savedProbe;
        NotificationService.Instance.Dispatch = _savedDispatch;
    }

    private AboutViewModel NewAbout(string clientBuildId, string hostBuildId)
    {
        var about = new AboutViewModel(new ConnectionViewModel(), null!)
        {
            ClientVersion = "9.8.7",
            ClientBuildId = clientBuildId,
            HostBuildId = hostBuildId,
        };
        _created.Add(about);
        return about;
    }

    [Fact]
    public async Task TheCommandCopiesTheVersionAndBuildAndTellsTheUser()
    {
        var about = NewAbout("abc1234", "def5678");
        string? copied = null;
        about.CopyToClipboardAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await about.CopySupportDetailsCommand.ExecuteAsync(null);

        copied.Should().Contain("RemEx 9.8.7 (build abc1234)");
        copied.Should().Contain("(build def5678)");
        copied.Should().Contain("System: ").And.Contain("Language: ");
        _announced.Should().ContainSingle();
        _announced[0].Title.Should().NotBe("About_DetailsCopied", "the toast must carry resolved text, not the key");
    }

    [Fact]
    public void AnEmptyBuildIdLeavesTheBuildFragmentOut()
    {
        var text = NewAbout(clientBuildId: "", hostBuildId: "").BuildSupportDetails();

        text.Should().Contain("RemEx 9.8.7").And.NotContain("build");
    }

    [Fact]
    public async Task WithNoClipboardWiredNothingIsAnnounced()
    {
        var about = NewAbout("abc1234", "");

        await about.CopySupportDetailsCommand.ExecuteAsync(null);

        _announced.Should().BeEmpty("a copy that never happened must not claim it did");
    }

    private sealed class RecordingSink(List<(NotificationImportance, string, string)> sink) : IInAppNotificationSink
    {
        public void Show(NotificationImportance importance, string title, string message)
            => sink.Add((importance, title, message));
    }
}
