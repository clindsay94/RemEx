using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Agent.Services.Launching;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Tests;

/// <summary>
/// The fallback POLICY of <see cref="WindowsUnelevatedLauncher"/>, against a fake
/// <see cref="IShellAccess"/> (security review of RemEx-pp4cm.2). The rule under test: a link,
/// document or folder must never end up opened with administrator rights because Explorer had a bad
/// moment, and a shell whose own token is elevated or foreign is not a normal-permission source.
/// Programs may still fall back, which is no worse than before.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnelevatedLauncherPolicyTests
{
    private const string Url = "https://github.com/clindsay94/remex";
    private static readonly string Program = Path.Combine(Path.GetTempPath(), "remex-policy-tool.exe");

    private readonly FakeShellAccess _shell = new();
    private readonly ListLogger _log = new();

    private UnelevatedLaunchResult Launch(string target, string? arguments = null)
        => new WindowsUnelevatedLauncher(_log, _shell)
            .TryLaunch(target, arguments, null, WindowsUnelevatedLauncher.SwShowNormal, out _);

    // ── #1: the shell route never turns into an elevated launch ───────────────────────────────

    [Fact]
    public void AUrlIsNeverHandedBackForAnElevatedLaunchWhenTheShellFails()
    {
        // FindWindowSW returning nothing (Explorer restarting) surfaces as an exception from the
        // shell call; so does RPC_E_CALL_REJECTED. Retry, explorer.exe last resort, then Failed.
        _shell.ShellExecuteFailures = int.MaxValue;
        _shell.TokenStartThrows = new Win32Exception(1058, "The Secondary Logon service is disabled.");

        var result = Launch(Url);

        Assert.Equal(UnelevatedLaunchResult.Failed, result);
        Assert.Equal(2, _shell.ShellExecuteCalls);
        Assert.Single(_shell.Delays);
        var lastResort = Assert.Single(_shell.TokenStarts);
        Assert.EndsWith("explorer.exe", lastResort.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\"" + Url + "\"", lastResort.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains(Url, StringComparison.Ordinal));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ATransientShellFailureIsRetriedOnceAfterAShortDelay()
    {
        _shell.ShellExecuteFailures = 1;

        Assert.Equal(UnelevatedLaunchResult.Launched, Launch(Url));
        Assert.Equal(2, _shell.ShellExecuteCalls);
        Assert.Equal([WindowsUnelevatedLauncher.ShellRetryDelay], _shell.Delays);
        Assert.Empty(_shell.TokenStarts);
    }

    [Fact]
    public void WhenTheShellStaysUnreachableExplorerExeWithTheShellTokenIsTheLastResort()
    {
        _shell.ShellExecuteFailures = int.MaxValue;

        Assert.Equal(UnelevatedLaunchResult.Launched, Launch(Url));
        var start = Assert.Single(_shell.TokenStarts);
        Assert.Equal(FakeShellAccess.ShellPid, start.ShellProcessId);
        Assert.EndsWith("explorer.exe", start.Executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALinkTooLongForTheExplorerLastResortFailsRatherThanFallingBack()
    {
        _shell.ShellExecuteFailures = int.MaxValue;
        var longUrl = Url + "?q=" + new string('a', WindowsUnelevatedLauncher.MaxTokenCommandLineChars);

        Assert.Equal(UnelevatedLaunchResult.Failed, Launch(longUrl));
        Assert.Empty(_shell.TokenStarts);
    }

    [Fact]
    public void OnlyAGenuinelyAbsentShellCountsAsNoShell()
    {
        _shell.ShellWindow = 0;

        Assert.Equal(UnelevatedLaunchResult.NoShell, Launch(Url));
        Assert.Equal(0, _shell.ShellExecuteCalls);
        Assert.Empty(_shell.TokenStarts);
    }

    [Fact]
    public void ANonElevatedRemExNeedsNothing()
    {
        _shell.IsElevated = false;

        Assert.Equal(UnelevatedLaunchResult.NotNeeded, Launch(Url));
        Assert.Equal(0, _shell.ShellExecuteCalls);
    }

    // ── #2: the shell's own token must be a normal-permission source ──────────────────────────

    public static TheoryData<int, int, bool> UnusableShellTokens => new()
    {
        // elevation type, session, token readable
        { ShellTokenInfo.TokenElevationTypeFull, FakeShellAccess.Session, true },  // elevated Explorer
        { 3, FakeShellAccess.Session + 1, true },                                  // another session
        { 3, FakeShellAccess.Session, false },                                     // unreadable
    };

    [Theory]
    [MemberData(nameof(UnusableShellTokens))]
    public void AnUnusableShellTokenFailsALinkWithoutTouchingTheShell(int elevationType, int session, bool readable)
    {
        _shell.Token = readable ? new ShellTokenInfo(elevationType, session) : null;

        Assert.Equal(UnelevatedLaunchResult.Failed, Launch(Url));
        Assert.Equal(0, _shell.ShellExecuteCalls);
        Assert.Empty(_shell.TokenStarts);
    }

    [Theory]
    [MemberData(nameof(UnusableShellTokens))]
    public void AnUnusableShellTokenLetsAProgramFallBackAsBefore(int elevationType, int session, bool readable)
    {
        _shell.Token = readable ? new ShellTokenInfo(elevationType, session) : null;

        Assert.Equal(UnelevatedLaunchResult.ProgramRouteUnavailable, Launch(Program));
        Assert.Empty(_shell.TokenStarts);
    }

    [Fact]
    public void ADifferentUserOnTheShellIsNotAReasonToRefuse()
    {
        // Over-the-shoulder elevation: RemEx runs as an admin account, Explorer as the signed-in
        // user. Only elevation type and session are checked, so a Limited token in our session is fine.
        _shell.Token = new ShellTokenInfo(3, FakeShellAccess.Session);

        Assert.Equal(UnelevatedLaunchResult.Launched, Launch(Url));
        Assert.Equal(UnelevatedLaunchResult.Launched, Launch(Program));
    }

    // ── #3 and the program route ──────────────────────────────────────────────────────────────

    [Fact]
    public void AProgramWhoseCommandLineIsTooLongFallsBackWithoutTrying()
    {
        var args = new string('x', WindowsUnelevatedLauncher.MaxTokenCommandLineChars);

        Assert.Equal(UnelevatedLaunchResult.ProgramRouteUnavailable, Launch(Program, args));
        Assert.Empty(_shell.TokenStarts);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("longer than", StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramFallsBackWhenSecondaryLogonIsUnavailable()
    {
        _shell.TokenStartThrows = new Win32Exception(1058);

        Assert.Equal(UnelevatedLaunchResult.ProgramRouteUnavailable, Launch(Program));
        Assert.Single(_shell.TokenStarts);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void AProgramThatNeedsAdministratorRightsIsReportedAsSuch()
    {
        _shell.TokenStartResult = TokenStartResult.ElevationRequired;

        Assert.Equal(UnelevatedLaunchResult.ElevationRequired, Launch(Program));
    }

    [Fact]
    public void AProgramStartsWithTheShellTokenAndItsOwnFolder()
    {
        Assert.Equal(UnelevatedLaunchResult.Launched, Launch(Program, "--flag"));
        var start = Assert.Single(_shell.TokenStarts);
        Assert.Equal(Program, start.Executable);
        Assert.Equal("\"" + Program + "\" --flag", start.CommandLine);
        Assert.Equal(Path.GetDirectoryName(Program), start.WorkingDirectory);
        Assert.Equal(0, _shell.ShellExecuteCalls);
    }

    private sealed class FakeShellAccess : IShellAccess
    {
        public const int Session = 1;
        public const int ShellPid = 4242;

        public bool IsElevated { get; set; } = true;
        public int CurrentSessionId => Session;
        public nint ShellWindow { get; set; } = 0x1234;
        public ShellTokenInfo? Token { get; set; } = new(3, Session);
        public int ShellExecuteFailures { get; set; }
        public int ShellExecuteCalls { get; private set; }
        public Exception? TokenStartThrows { get; set; }
        public TokenStartResult TokenStartResult { get; set; } = TokenStartResult.Started;
        public List<TimeSpan> Delays { get; } = [];
        public List<(int ShellProcessId, string Executable, string CommandLine, string? WorkingDirectory)> TokenStarts { get; } = [];

        public nint GetShellWindow() => ShellWindow;
        public int GetShellProcessId(nint shellWindow) => ShellPid;
        public ShellTokenInfo? QueryShellToken(int shellProcessId) => Token;
        public ShortcutInfo ReadShortcut(string shortcutPath) => throw new NotSupportedException();
        public void Delay(TimeSpan delay) => Delays.Add(delay);

        public void ShellExecuteInDesktopShell(nint shellWindow, string target, string? arguments, string? workingDirectory, short showCommand)
        {
            ShellExecuteCalls++;
            if (ShellExecuteCalls <= ShellExecuteFailures)
                throw new COMException("Explorer's desktop window is not registered.");
        }

        public TokenStartResult StartWithShellToken(
            int shellProcessId, string executable, string commandLine, string? workingDirectory, short showCommand, out int processId)
        {
            TokenStarts.Add((shellProcessId, executable, commandLine, workingDirectory));
            processId = 0;
            if (TokenStartThrows is not null)
                throw TokenStartThrows;
            processId = 777;
            return TokenStartResult;
        }
    }

    private sealed class ListLogger : ILogger<WindowsUnelevatedLauncher>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
