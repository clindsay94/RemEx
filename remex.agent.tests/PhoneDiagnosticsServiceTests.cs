using Microsoft.Extensions.Logging;
using Remex.Agent.Handlers;
using Remex.Agent.Services.Diagnostics;
using Remex.Agent.Services.Security;
using Remex.Core.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Readiness;
using Remex.Core.Validation;

namespace Remex.Agent.Tests;

/// <summary>
/// The phone's read-only view of the PC log (RemEx-pp4cm.13): incremental reads by sequence number, the
/// level filter, the page and size caps, redaction on the way out, the per-session rate limit, and
/// refusal of anything that is not a proven, paired, non-loopback phone.
/// </summary>
public sealed class PhoneDiagnosticsServiceTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void AdvanceMs(long ms) => _ticks += ms;
    }

    private sealed class FakeReadiness(params ReadinessCheck[] checks) : ISystemReadinessService
    {
        public SystemReadinessReport Run(bool forceFresh = false) => new(checks);
    }

    private static LogEntry Entry(long seq, LogLevel level, string message, string category = "Remex.Test", Exception? ex = null) =>
        new(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc).AddSeconds(seq), level, category, message, ex) { Seq = seq };

    private static (PhoneDiagnosticsService Service, ManualTime Time) Make(
        IReadOnlyList<LogEntry> entries, ISystemReadinessService? readiness = null, HostCapabilities? caps = null)
    {
        var time = new ManualTime();
        var head = entries.Count == 0 ? 0 : entries[^1].Seq;
        var service = new PhoneDiagnosticsService(
            () => entries,
            () => head,
            readiness,
            () => caps ?? new HostCapabilities { Version = "3.0.0.1", SupportsRemoteDesktop = true, Platform = "windows" },
            () => TimeSpan.FromMinutes(125),
            time);
        return (service, time);
    }

    private static List<LogEntry> Numbered(int count, LogLevel level = LogLevel.Information) =>
        Enumerable.Range(1, count).Select(i => Entry(i, level, "line " + i)).ToList();

    private static RemexMessage LogsGet(long after = 0, string level = "information", int max = 200) => new()
    {
        Type = MessageTypes.DiagnosticLogsGet,
        CorrelationId = "corr-1",
        DiagnosticLogsRequest = new DiagnosticLogsRequest { AfterSeq = after, MinLevel = level, Max = max },
    };

    private static Task<RemexMessage> AskAsync(PhoneDiagnosticsService svc, RemexMessage msg, PhoneDiagnosticsService.SessionGate? gate = null,
        bool loopback = false, bool proven = true) =>
        svc.HandleAsync(msg, gate ?? svc.CreateSessionGate(), loopback, proven, CancellationToken.None);

    // ── incremental reads, filter, caps ─────────────────────────────────────────────────────────

    [Fact]
    public void AfterSeqZeroReturnsTheNewestPageOldestFirst()
    {
        var (svc, _) = Make(Numbered(10));

        var page = svc.BuildLogs(new DiagnosticLogsRequest { AfterSeq = 0, MinLevel = "trace", Max = 4 });

        Assert.Equal([7L, 8, 9, 10], page.Entries.Select(e => e.Seq));
        Assert.True(page.Truncated);
        Assert.Equal(10, page.LastSeq);
    }

    [Fact]
    public void AnIncrementalReadReturnsOnlyWhatCameAfterAndNeverSkipsALine()
    {
        var (svc, _) = Make(Numbered(10));

        var first = svc.BuildLogs(new DiagnosticLogsRequest { AfterSeq = 2, MinLevel = "trace", Max = 3 });
        Assert.Equal([3L, 4, 5], first.Entries.Select(e => e.Seq));
        Assert.True(first.Truncated);
        Assert.Equal(5, first.LastSeq);

        var rest = svc.BuildLogs(new DiagnosticLogsRequest { AfterSeq = first.LastSeq, MinLevel = "trace", Max = 50 });
        Assert.Equal([6L, 7, 8, 9, 10], rest.Entries.Select(e => e.Seq));
        Assert.False(rest.Truncated);
        Assert.Equal(10, rest.LastSeq);
    }

    [Fact]
    public void NothingNewReturnsAnEmptyPageAtTheHead()
    {
        var (svc, _) = Make(Numbered(5));

        var page = svc.BuildLogs(new DiagnosticLogsRequest { AfterSeq = 5, MinLevel = "trace", Max = 50 });

        Assert.Empty(page.Entries);
        Assert.False(page.Truncated);
        Assert.Equal(5, page.LastSeq);
    }

    [Fact]
    public void ASequenceBeyondTheHeadMeansTheHostRestartedSoTheNewestPageIsServed()
    {
        var (svc, _) = Make(Numbered(3));

        var page = svc.BuildLogs(new DiagnosticLogsRequest { AfterSeq = 9000, MinLevel = "trace", Max = 50 });

        Assert.Equal([1L, 2, 3], page.Entries.Select(e => e.Seq));
    }

    [Fact]
    public void MinLevelFiltersAndTheCursorStillAdvancesPastHiddenLines()
    {
        var entries = new List<LogEntry>
        {
            Entry(1, LogLevel.Debug, "d"), Entry(2, LogLevel.Information, "i"), Entry(3, LogLevel.Warning, "w"),
            Entry(4, LogLevel.Error, "e"), Entry(5, LogLevel.Debug, "d2"),
        };
        var (svc, _) = Make(entries);

        var warnings = svc.BuildLogs(new DiagnosticLogsRequest { MinLevel = "warning", Max = 50 });

        Assert.Equal([3L, 4], warnings.Entries.Select(e => e.Seq));
        Assert.All(warnings.Entries, e => Assert.Contains(e.Level, new[] { "warning", "error" }));
        // The debug line at 5 was looked at and hidden; the next poll must not rescan it.
        Assert.Equal(5, warnings.LastSeq);
    }

    [Fact]
    public async Task MaxIsEnforcedAndAnOutOfRangeMaxIsRejected()
    {
        var (svc, _) = Make(Numbered(800));

        var ok = await AskAsync(svc, LogsGet(max: 500));
        Assert.Equal(500, ok.DiagnosticLogsResponse!.Entries.Count);
        Assert.True(ok.DiagnosticLogsResponse.Truncated);

        var tooMany = await AskAsync(svc, LogsGet(max: 501));
        Assert.Equal("invalid_request", tooMany.DiagnosticLogsResponse!.Error);
        Assert.Empty(tooMany.DiagnosticLogsResponse.Entries);

        var badLevel = await AskAsync(svc, LogsGet(level: "verbose"));
        Assert.Equal("invalid_request", badLevel.DiagnosticLogsResponse!.Error);

        var missing = await AskAsync(svc, new RemexMessage { Type = MessageTypes.DiagnosticLogsGet, CorrelationId = "c" });
        Assert.Equal("invalid_request", missing.DiagnosticLogsResponse!.Error);
    }

    [Fact]
    public void TheResponseIsCappedNearTwoHundredFiftySixKilobytesAndSaysSo()
    {
        var big = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 105));
        var entries = Enumerable.Range(1, 500).Select(i => Entry(i, LogLevel.Information, big + " #" + i)).ToList();
        var (svc, _) = Make(entries);

        var page = svc.BuildLogs(new DiagnosticLogsRequest { MinLevel = "trace", Max = 500 });

        Assert.True(page.Truncated);
        Assert.InRange(page.Entries.Count, 1, 499);
        var bytes = page.Entries.Sum(e => System.Text.Encoding.UTF8.GetByteCount(e.Message) + 110);
        Assert.True(bytes <= DiagnosticsValidation.MaxResponseBytes, $"{bytes} bytes");
        // The newest lines are the ones kept.
        Assert.Equal(500, page.Entries[^1].Seq);
    }

    [Fact]
    public void ALongMessageIsCut()
    {
        var (svc, _) = Make([Entry(1, LogLevel.Error, string.Concat(Enumerable.Repeat("word ", 2000)))]);

        var page = svc.BuildLogs(new DiagnosticLogsRequest { MinLevel = "trace", Max = 5 });

        Assert.Equal(DiagnosticsValidation.MaxMessageLength + 1, page.Entries[0].Message.Length);
        Assert.EndsWith("…", page.Entries[0].Message);
    }

    // ── redaction ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATokenAnIpAndAPathAreMaskedBeforeTheyLeaveThePc()
    {
        var entries = new List<LogEntry>
        {
            Entry(1, LogLevel.Error,
                "Phone 192.168.1.77:5005 sent token=AbCdEf123456 and Bearer eyJhbGciOi.payload.sig; " +
                @"file C:\Users\Connor\Secret Stuff\passwords.kdbx and /home/connor/.ssh/id_rsa " +
                "client 3f9a1b2c-4d5e-6f70-8192-a3b4c5d6e7f8 mail connor@example.com"),
        };
        var (svc, _) = Make(entries);

        var message = svc.BuildLogs(new DiagnosticLogsRequest { MinLevel = "trace", Max = 5 }).Entries[0].Message;

        Assert.DoesNotContain("192.168.1.77", message);
        Assert.DoesNotContain("AbCdEf123456", message);
        Assert.DoesNotContain("eyJhbGciOi", message);
        Assert.DoesNotContain("Connor", message);
        Assert.DoesNotContain("Secret Stuff", message);
        Assert.DoesNotContain("/home/connor", message);
        Assert.DoesNotContain("a3b4c5d6e7f8", message);
        Assert.DoesNotContain("example.com", message);
        Assert.Contains("<ip>", message);
        Assert.Contains("token=<redacted>", message);
        Assert.Contains("<path>/passwords.kdbx", message);
        Assert.Contains("3f9a1b2c…", message);
    }

    [Fact]
    public void AnExceptionIsReducedToItsTypeAndFirstLineAndThatLineIsRedacted()
    {
        var ex = new InvalidOperationException("Could not open C:\\Users\\Connor\\x.db\nSECOND LINE with token=hunter2\n   at Foo.Bar()");
        var (svc, _) = Make([Entry(1, LogLevel.Error, "Boom", ex: ex)]);

        var message = svc.BuildLogs(new DiagnosticLogsRequest { MinLevel = "trace", Max = 5 }).Entries[0].Message;

        Assert.Contains("InvalidOperationException", message);
        Assert.DoesNotContain("SECOND LINE", message);
        Assert.DoesNotContain("hunter2", message);
        Assert.DoesNotContain("Foo.Bar", message);
        Assert.DoesNotContain("Connor", message);
    }

    [Fact]
    public void RedactionLeavesRoutesTimesAndVersionsReadable()
    {
        var text = LogRedaction.RedactText("Rejected /ws/desktop at 12:30:45 for agent 10.0.26300 on port 5005");

        Assert.Equal("Rejected /ws/desktop at 12:30:45 for agent 10.0.26300 on port 5005", text);
    }

    [Theory]
    [InlineData("addr fe80::1ff:fe23:4567:890a here")]
    [InlineData("addr 2001:db8:85a3:0:0:8a2e:370:7334 here")]
    public void AnIpv6AddressIsMasked(string text) => Assert.Contains("<ip>", LogRedaction.RedactText(text));

    [Fact]
    public void ALongKeyLikeStringIsMaskedWhateverItIsCalled()
    {
        var text = LogRedaction.RedactText("derived key QWxhZGRpbjpvcGVuIHNlc2FtZVF1aWNrQnJvd25Gb3g= ok");

        Assert.DoesNotContain("QWxhZGRpbjpvcGVuIHNlc2FtZVF1aWNrQnJvd25Gb3g", text);
    }

    // ── who may ask, and how often ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task LoopbackAndUnprovenSessionsAreRefusedForBothRequests(bool loopback, bool proven)
    {
        var (svc, _) = Make(Numbered(5));

        var logs = await AskAsync(svc, LogsGet(), loopback: loopback, proven: proven);
        var summary = await AskAsync(svc, new RemexMessage { Type = MessageTypes.DiagnosticSummaryGet, CorrelationId = "s" },
            loopback: loopback, proven: proven);

        Assert.Equal("refused", logs.DiagnosticLogsResponse!.Error);
        Assert.Empty(logs.DiagnosticLogsResponse.Entries);
        Assert.Equal("corr-1", logs.CorrelationId);
        Assert.Equal("refused", summary.DiagnosticSummaryResponse!.Error);
        Assert.Empty(summary.DiagnosticSummaryResponse.Items);
    }

    [Fact]
    public async Task AProvenNonLoopbackPhoneIsServedAndTheCorrelationIdIsEchoed()
    {
        var (svc, _) = Make(Numbered(5));

        var reply = await AskAsync(svc, LogsGet());

        Assert.Equal(MessageTypes.DiagnosticLogsResult, reply.Type);
        Assert.Equal("corr-1", reply.CorrelationId);
        Assert.Null(reply.DiagnosticLogsResponse!.Error);
        Assert.Equal(5, reply.DiagnosticLogsResponse.Entries.Count);
    }

    [Fact]
    public async Task ASecondRequestInsideASecondIsRateLimitedAndOneAfterItIsServed()
    {
        var (svc, time) = Make(Numbered(5));
        var gate = svc.CreateSessionGate();

        Assert.Null((await AskAsync(svc, LogsGet(), gate)).DiagnosticLogsResponse!.Error);

        time.AdvanceMs(400);
        var limited = await AskAsync(svc, LogsGet(), gate);
        Assert.Equal("rate_limited", limited.DiagnosticLogsResponse!.Error);
        Assert.Equal("corr-1", limited.CorrelationId);

        time.AdvanceMs(700);
        Assert.Null((await AskAsync(svc, LogsGet(), gate)).DiagnosticLogsResponse!.Error);
    }

    [Fact]
    public async Task TheRateLimitIsPerSessionAndPerRequestKind()
    {
        var (svc, _) = Make(Numbered(5));
        var phoneA = svc.CreateSessionGate();
        var phoneB = svc.CreateSessionGate();

        Assert.Null((await AskAsync(svc, LogsGet(), phoneA)).DiagnosticLogsResponse!.Error);

        // Another phone has its own budget.
        Assert.Null((await AskAsync(svc, LogsGet(), phoneB)).DiagnosticLogsResponse!.Error);

        // And the same phone may ask for the summary straight after the logs.
        var summary = await AskAsync(svc, new RemexMessage { Type = MessageTypes.DiagnosticSummaryGet }, phoneA);
        Assert.Null(summary.DiagnosticSummaryResponse!.Error);
    }

    [Fact]
    public async Task ARefusedRequestDoesNotSpendTheRateLimitBudget()
    {
        var (svc, _) = Make(Numbered(5));
        var gate = svc.CreateSessionGate();

        await AskAsync(svc, LogsGet(), gate, loopback: true);

        Assert.Null((await AskAsync(svc, LogsGet(), gate)).DiagnosticLogsResponse!.Error);
    }

    [Theory]
    [InlineData(MessageTypes.DiagnosticLogsGet)]
    [InlineData(MessageTypes.DiagnosticSummaryGet)]
    public void BothRequestsNeedPairing(string type) => Assert.True(PingPongHandler.RequiresPairing(type));

    // ── summary ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSummaryListsTheCardRowsPlusVersionAndUptime()
    {
        var readiness = new FakeReadiness(
            new ReadinessCheck(ReadinessCheckId.PortListening, ReadinessState.Ok, "listening on 0.0.0.0:5005"),
            new ReadinessCheck(ReadinessCheckId.Certificate, ReadinessState.Ok, "cert at C:\\ProgramData\\RemEx\\cert.pfx"),
            new ReadinessCheck(ReadinessCheckId.Firewall, ReadinessState.Problem, "no inbound rule"),
            new ReadinessCheck(ReadinessCheckId.Elevation, ReadinessState.NotApplicable, ""),
            new ReadinessCheck(ReadinessCheckId.Autostart, ReadinessState.Unknown, "UI not ready"));
        var (svc, _) = Make(Numbered(1), readiness);

        var reply = await AskAsync(svc, new RemexMessage { Type = MessageTypes.DiagnosticSummaryGet, CorrelationId = "s1" });
        var items = reply.DiagnosticSummaryResponse!.Items.ToDictionary(i => i.Key);

        Assert.Equal("s1", reply.CorrelationId);
        Assert.Equal("ok", items["listener"].State);
        Assert.Equal("ok", items["certificate"].State);
        Assert.Equal("error", items["firewall"].State);
        Assert.Equal("warn", items["autostart"].State);
        Assert.DoesNotContain("elevation", items.Keys);
        Assert.Equal("ok", items["capture"].State);
        Assert.Equal("3.0.0.1", items["version"].Detail);
        Assert.Equal("2h 5m", items["uptime"].Detail);
        Assert.All(reply.DiagnosticSummaryResponse.Items, i => Assert.True(DiagnosticsValidation.IsValidState(i.State)));
        // Detail text is redacted like everything else.
        Assert.DoesNotContain("0.0.0.0", items["listener"].Detail);
        Assert.DoesNotContain("ProgramData", items["certificate"].Detail);
    }

    [Fact]
    public async Task WithoutAReadinessServiceTheRowsAreShownAsWarningsRatherThanDropped()
    {
        var (svc, _) = Make(Numbered(1), readiness: null,
            caps: new HostCapabilities { Version = "3.0.0", SupportsRemoteDesktop = false, RemoteDesktopUnavailableReason = "No display" });

        var items = (await AskAsync(svc, new RemexMessage { Type = MessageTypes.DiagnosticSummaryGet }))
            .DiagnosticSummaryResponse!.Items.ToDictionary(i => i.Key);

        Assert.Equal("warn", items["listener"].State);
        Assert.Equal("warn", items["certificate"].State);
        Assert.Equal("error", items["capture"].State);
        Assert.Equal("No display", items["capture"].Detail);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "59m")]
    [InlineData(125, "2h 5m")]
    [InlineData(3 * 24 * 60 + 2 * 60 + 7, "3d 2h 7m")]
    public void UptimeReadsInWholeUnits(int minutes, string expected) =>
        Assert.Equal(expected, PhoneDiagnosticsService.FormatUptime(TimeSpan.FromMinutes(minutes)));
}
