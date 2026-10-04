using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// <c>diagnostic_logs_get</c> / <c>diagnostic_summary_get</c> and their answers survive the wire, a
/// malformed payload costs its own slot rather than the session, and the request limits hold
/// (RemEx-pp4cm.13).
/// </summary>
/// <remarks>
/// The hand-written JSON is the shape the Kotlin side builds and parses, for the reason
/// <c>HomePinsWireTests</c> gives: a round trip that only compares this serializer to itself would pass
/// on a field name only the PC agrees with.
/// </remarks>
public class DiagnosticsWireTests
{
    private static string Serialize(RemexMessage m) =>
        RemexJson.Serialize(m, RemexJsonSerializerContext.Relaxed.RemexMessage);

    private static RemexMessage? Deserialize(string json) =>
        RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

    [Fact]
    public void ALogsRequestSurvivesTheRoundTrip()
    {
        var sent = new RemexMessage
        {
            Type = MessageTypes.DiagnosticLogsGet,
            CorrelationId = "c1",
            DiagnosticLogsRequest = new DiagnosticLogsRequest { AfterSeq = 42, MinLevel = "warning", Max = 250 },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.DiagnosticLogsRequest;

        Assert.Contains("\"diagnosticLogsRequest\":", json, StringComparison.Ordinal);
        Assert.Contains("\"afterSeq\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"minLevel\":\"warning\"", json, StringComparison.Ordinal);
        Assert.Equal(new DiagnosticLogsRequest { AfterSeq = 42, MinLevel = "warning", Max = 250 }, arrived);
    }

    [Fact]
    public void ALogsResponseSurvivesTheRoundTrip()
    {
        var time = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero);
        var sent = new RemexMessage
        {
            Type = MessageTypes.DiagnosticLogsResult,
            DiagnosticLogsResponse = new DiagnosticLogsResponse
            {
                Entries =
                [
                    new DiagnosticLogEntry { Seq = 7, TimeUtc = time, Level = "error", Category = "Remex.Test", Message = "Ünïcødé failed" },
                ],
                LastSeq = 9,
                Truncated = true,
            },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.DiagnosticLogsResponse;

        Assert.Contains("\"diagnosticLogsResponse\":", json, StringComparison.Ordinal);
        Assert.Contains("\"lastSeq\":9", json, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":true", json, StringComparison.Ordinal);
        Assert.NotNull(arrived);
        var entry = Assert.Single(arrived!.Entries);
        Assert.Equal(7, entry.Seq);
        Assert.Equal(time, entry.TimeUtc);
        Assert.Equal("error", entry.Level);
        Assert.Equal("Ünïcødé failed", entry.Message);
        Assert.Equal(9, arrived.LastSeq);
        Assert.True(arrived.Truncated);
        Assert.Null(arrived.Error);
    }

    [Fact]
    public void ASummaryResponseSurvivesTheRoundTrip()
    {
        var sent = new RemexMessage
        {
            Type = MessageTypes.DiagnosticSummaryResult,
            DiagnosticSummaryResponse = new DiagnosticSummaryResponse
            {
                Items =
                [
                    new DiagnosticSummaryItem { Key = "listener", State = "ok", Detail = "port 5005" },
                    new DiagnosticSummaryItem { Key = "firewall", State = "warn" },
                ],
            },
        };

        var json = Serialize(sent);
        var arrived = Deserialize(json)!.DiagnosticSummaryResponse;

        Assert.Contains("\"diagnosticSummaryResponse\":", json, StringComparison.Ordinal);
        Assert.Equal(2, arrived!.Items.Count);
        Assert.Equal("listener", arrived.Items[0].Key);
        Assert.Equal("warn", arrived.Items[1].State);
    }

    [Fact]
    public void AnErrorResponseCarriesOnlyAToken()
    {
        var json = Serialize(new RemexMessage
        {
            Type = MessageTypes.DiagnosticLogsResult,
            DiagnosticLogsResponse = new DiagnosticLogsResponse { Error = "rate_limited" },
        });

        Assert.Equal("rate_limited", Deserialize(json)!.DiagnosticLogsResponse!.Error);
    }

    [Fact]
    public void TheKotlinShapedRequestParses()
    {
        const string json =
            """{"type":"diagnostic_logs_get","protocolVersion":2,"correlationId":"x","diagnosticLogsRequest":{"afterSeq":0,"minLevel":"information","max":200}}""";

        var msg = Deserialize(json)!;

        Assert.Equal(MessageTypes.DiagnosticLogsGet, msg.Type);
        Assert.Equal(0, msg.DiagnosticLogsRequest!.AfterSeq);
        Assert.Equal(200, msg.DiagnosticLogsRequest.Max);
    }

    [Fact]
    public void AMalformedPayloadCostsItsOwnSlotNotTheEnvelope()
    {
        const string json =
            """{"type":"diagnostic_logs_get","protocolVersion":2,"correlationId":"x","diagnosticLogsRequest":{"afterSeq":"soon","max":"lots"}}""";

        var msg = Deserialize(json);

        Assert.NotNull(msg);
        Assert.Equal(MessageTypes.DiagnosticLogsGet, msg!.Type);
        Assert.Equal("x", msg.CorrelationId);
        Assert.Null(msg.DiagnosticLogsRequest);
    }

    [Fact]
    public void EveryDiagnosticTypeStartsWithTheForwardedPrefix()
    {
        // The Android router forwards the whole family by this prefix. A type outside it would be
        // dropped in silence on the phone.
        foreach (var type in new[]
                 {
                     MessageTypes.DiagnosticLogsGet, MessageTypes.DiagnosticLogsResult,
                     MessageTypes.DiagnosticSummaryGet, MessageTypes.DiagnosticSummaryResult,
                 })
        {
            Assert.StartsWith("diagnostic_", type, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHostToPhoneAnswersAreInTheAudienceTable()
    {
        Assert.Equal(ClientSurface.AndroidControl, MessageAudience.HostToClient[MessageTypes.DiagnosticLogsResult]);
        Assert.Equal(ClientSurface.AndroidControl, MessageAudience.HostToClient[MessageTypes.DiagnosticSummaryResult]);
    }
}

public class DiagnosticsValidationTests
{
    [Fact]
    public void ADefaultRequestIsValid() =>
        Assert.True(DiagnosticsValidation.IsValidLogsRequest(new DiagnosticLogsRequest()));

    [Theory]
    [InlineData(0L, "information", 1, true)]
    [InlineData(123456789012L, "critical", 500, true)]
    [InlineData(-1L, "information", 100, false)]
    [InlineData(0L, "information", 0, false)]
    [InlineData(0L, "information", 501, false)]
    [InlineData(0L, "information", -5, false)]
    [InlineData(0L, "INFORMATION", 100, false)]
    [InlineData(0L, "verbose", 100, false)]
    [InlineData(0L, "", 100, false)]
    public void TheLimitsHold(long afterSeq, string level, int max, bool valid) =>
        Assert.Equal(valid, DiagnosticsValidation.IsValidLogsRequest(
            new DiagnosticLogsRequest { AfterSeq = afterSeq, MinLevel = level, Max = max }));

    [Fact]
    public void ANullRequestIsInvalid() => Assert.False(DiagnosticsValidation.IsValidLogsRequest(null));

    [Fact]
    public void EveryWireLevelNameRoundTripsThroughItsRankAndRanksEqualLogLevel()
    {
        Assert.NotEmpty(DiagnosticsValidation.LevelNames);
        foreach (var (name, expected) in new[]
                 {
                     ("trace", LogLevel.Trace), ("debug", LogLevel.Debug), ("information", LogLevel.Information),
                     ("warning", LogLevel.Warning), ("error", LogLevel.Error), ("critical", LogLevel.Critical),
                 })
        {
            Assert.True(DiagnosticsValidation.TryGetLevelRank(name, out var rank), name);
            Assert.Equal(name, DiagnosticsValidation.LevelName(rank));
            // The host casts the rank straight to LogLevel, so the two must never drift apart.
            Assert.Equal((int)expected, rank);
            Assert.Contains(name, DiagnosticsValidation.LevelNames);
        }

        Assert.Equal(6, DiagnosticsValidation.LevelNames.Count);
        Assert.False(DiagnosticsValidation.TryGetLevelRank("none", out _));
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("warn", true)]
    [InlineData("error", true)]
    [InlineData("fine", false)]
    [InlineData(null, false)]
    public void OnlyThreeSummaryStatesAreKnown(string? state, bool valid) =>
        Assert.Equal(valid, DiagnosticsValidation.IsValidState(state));
}

public class InMemoryLogSinkSequenceTests
{
    [Fact]
    public void EntriesGetStrictlyIncreasingSequenceNumbersAndTheyOutliveAClear()
    {
        var before = InMemoryLogSink.LastSeq;
        var floor = InMemoryLogSink.MinimumLogLevel;
        InMemoryLogSink.MinimumLogLevel = LogLevel.Trace;
        try
        {
            InMemoryLogSink.Append(LogLevel.Information, "SeqTest", "one", null);
            InMemoryLogSink.Append(LogLevel.Information, "SeqTest", "two", null);
            var mine = InMemoryLogSink.GetEntries().Where(e => e.Category == "SeqTest").TakeLast(2).ToList();

            Assert.Equal(2, mine.Count);
            Assert.Equal(before + 1, mine[0].Seq);
            Assert.Equal(before + 2, mine[1].Seq);
            Assert.Equal(before + 2, InMemoryLogSink.LastSeq);

            InMemoryLogSink.Clear();
            Assert.Equal(before + 2, InMemoryLogSink.LastSeq);

            InMemoryLogSink.Append(LogLevel.Information, "SeqTest", "three", null);
            Assert.Equal(before + 3, InMemoryLogSink.GetEntries().Last().Seq);
        }
        finally
        {
            InMemoryLogSink.MinimumLogLevel = floor;
        }
    }
}
