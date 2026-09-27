using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Remex.Branding;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// The Live Handshake director against the shared vectors (RemEx-8g6n0, staging revision .3). The
/// Android director reads the same file, so a rule changed on one platform and not the other fails
/// here or there, never silently. Stepped the way the spec prescribes: <c>now</c> from 0 in 1 ms
/// increments, hand-off expected within 2 ms, plus the staged lock and answer show times.
/// </summary>
public class LiveHandshakeDirectorTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors().RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void TheConstantsMatchTheVectorFile()
    {
        var k = Vectors().RootElement.GetProperty("constants");
        void Check(float actual, string name) =>
            actual.Should().BeApproximately(k.GetProperty(name).GetSingle(), 1e-6f, name);
        Check(LiveHandshakeDirector.Floor, "floor");
        Check(LiveHandshakeDirector.Grace, "grace");
        Check(LiveHandshakeDirector.Cap, "cap");
        Check(LiveHandshakeDirector.LockHold, "lockHold");
        Check(LiveHandshakeDirector.Exit, "exit");
        Check(LiveHandshakeDirector.FadeExit, "fadeExit");
        Check(LiveHandshakeDirector.FirstPulse, "firstPulse");
        Check(LiveHandshakeDirector.PulsePeriod, "pulsePeriod");
        Check(LiveHandshakeDirector.AnswerMin, "answerMin");
        Check(LiveHandshakeDirector.AnswerGap, "answerGap");
        Check(LiveHandshakeDirector.LockAfter, "lockAfter");
        Check(LiveHandshakeDirector.LineGap, "lineGap");
    }

    [Fact]
    public void TheVectorFileHasCases()
    {
        CaseNames().Should().HaveCountGreaterThan(10, "the shared vectors must actually be read");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EachVectorHandsOffOnTimeWithTheRightStaging(string name)
    {
        var c = Vectors().RootElement.GetProperty("cases").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == name);

        var answers = c.GetProperty("answers").EnumerateArray()
            .Select(a => new HandshakeAnswer(a.GetProperty("at").GetSingle(),
                a.TryGetProperty("target", out var tg) && tg.GetBoolean()))
            .ToArray();
        var timeline = new HandshakeTimeline(
            Peers: c.GetProperty("peers").GetInt32(),
            TargetAt: c.GetProperty("hasTarget").GetBoolean() ? 0f : null,
            ReadyAt: Opt(c, "readyAt"),
            LinkedAt: Opt(c, "linkedAt"),
            FailedAt: Opt(c, "failedAt"),
            SkipAt: Opt(c, "skipAt"),
            ExitFromMark: c.GetProperty("exitFromMark").GetBoolean(),
            Answers: answers);
        float expected = c.GetProperty("expectHandoff").GetSingle();

        // Step now from 0 in 1 ms increments; the first step that reports a hand-off is the frame
        // the splash would start its exit on.
        float? handoff = null;
        double stepNow = 0;
        for (int ms = 0; ms <= 4000 && handoff is null; ms++)
        {
            stepNow = ms / 1000.0;
            handoff = LiveHandshakeDirector.Handoff(timeline, (float)stepNow);
        }

        handoff.Should().NotBeNull($"'{name}' must hand off within the cap");
        stepNow.Should().BeApproximately(expected, 0.002, $"'{name}': the frame the hand-off starts on");
        float h = handoff!.Value;
        h.Should().BeApproximately(expected, 0.002f, $"'{name}': the hand-off time");

        LiveHandshakeDirector.Origin(timeline, h).Should().Be(
            c.GetProperty("expectOrigin").GetString() == "target" ? HandshakeOrigin.Target : HandshakeOrigin.Mark,
            $"'{name}': where the portal opens");

        var lockShown = LiveHandshakeDirector.LockShownBy(timeline, h);
        var expectLock = Opt(c, "expectLockShown");
        if (expectLock is null) lockShown.Should().BeNull($"'{name}': no lock is shown");
        else lockShown.Should().NotBeNull().And.BeApproximately(expectLock.Value, 0.002f, $"'{name}': the lock's shown time");

        Span<float> staged = stackalloc float[LiveHandshakeDirector.MaxAnswers + 1];
        int slots = LiveHandshakeDirector.StageAnswers(timeline, h, staged, out _);
        var shown = staged[..slots].ToArray().Where(s => !float.IsNaN(s) && s < h).OrderBy(s => s).ToArray();
        var expectShown = c.GetProperty("expectAnswersShown").EnumerateArray().Select(a => a.GetSingle()).ToArray();
        shown.Should().Equal(expectShown, (a, b) => Math.Abs(a - b) < 0.002f, $"'{name}': answers shown before the hand-off");

        var pulses = Enumerable.Range(0, LiveHandshakeDirector.PulsesStarted(10f, handoff, reducedMotion: false))
            .Select(LiveHandshakeDirector.PulseAt).ToArray();
        var expectedPulses = c.GetProperty("expectPulses").EnumerateArray().Select(p => p.GetSingle()).ToArray();
        pulses.Should().Equal(expectedPulses, (a, b) => Math.Abs(a - b) < 0.001f, $"'{name}': pulses fire strictly before the hand-off");
    }

    [Fact]
    public void OnceDecidedTheHandoffIgnoresLaterEvents()
    {
        // "links after the grace window": the late link must not move a hand-off already decided.
        var before = new HandshakeTimeline(2, 0f, 0.70f, null, null, null, false);
        var after = before with { LinkedAt = 2.30f };

        LiveHandshakeDirector.Handoff(before, 3f).Should().BeApproximately(1.90f, 1e-4f);
        LiveHandshakeDirector.Handoff(after, 3f).Should().BeApproximately(1.90f, 1e-4f);
    }

    [Fact]
    public void NeverReadyStillHandsOffAtTheCap()
    {
        var timeline = new HandshakeTimeline(0, null, null, null, null, null, true);
        LiveHandshakeDirector.Handoff(timeline, 2.99f).Should().BeNull();
        LiveHandshakeDirector.Handoff(timeline, 3.00f).Should().BeApproximately(LiveHandshakeDirector.Cap, 1e-4f);
    }

    [Fact]
    public void ReducedMotionFiresNoPulses()
    {
        LiveHandshakeDirector.PulsesStarted(5f, 3.0f, reducedMotion: true).Should().Be(0);
        LiveHandshakeDirector.PulsesStarted(5f, 3.0f, reducedMotion: false).Should().Be(3);
    }

    [Fact]
    public void ThePcConsoleQueuesItsLinesLineGapApart()
    {
        // The lab's "pc" scenario: 3 phones, listener at 0.40, the S26 lock shown at 1.36, hand-off 2.06.
        var lines = new HandshakeLine[4];
        int n = LiveHandshakeDirector.PcConsole(3, 0.40f, 5005, "Galaxy S26 Ultra", 1.36f, 2.06f, lines);

        n.Should().Be(4);
        lines[0].Kind.Should().Be(HandshakeLineKind.Paired);
        lines[0].Count.Should().Be(3);
        lines[0].At.Should().BeApproximately(0.32f, 1e-4f);
        lines[1].Kind.Should().Be(HandshakeLineKind.Listening);
        lines[1].Port.Should().Be(5005);
        lines[1].At.Should().BeApproximately(0.64f, 1e-4f, "due 0.40 but queued LINE_GAP after the first line");
        lines[2].Kind.Should().Be(HandshakeLineKind.Linked);
        lines[2].Name.Should().Be("Galaxy S26 Ultra");
        lines[2].Hot.Should().BeTrue();
        lines[2].At.Should().BeApproximately(1.36f, 1e-4f);
        lines[3].Kind.Should().Be(HandshakeLineKind.Opening);
        lines[3].At.Should().BeApproximately(2.06f, 1e-4f);
    }

    [Fact]
    public void TheListeningLineIsNeverShownBeforeTheMarkHasLit()
    {
        // On the PC the host is usually up before the first frame (listeningAt = 0).
        var lines = new HandshakeLine[4];
        int n = LiveHandshakeDirector.PcConsole(0, 0f, null, null, null, null, lines);

        n.Should().Be(2);
        lines[0].Kind.Should().Be(HandshakeLineKind.NonePaired);
        lines[1].Kind.Should().Be(HandshakeLineKind.Listening);
        lines[1].Port.Should().BeNull("an unresolvable port is omitted, not guessed");
        lines[1].At.Should().BeApproximately(0.64f, 1e-4f, "max(0, ListeningMin 0.4) queued after the 0.32 line");
    }

    [Fact]
    public void ALockStagedPastTheHandoffIsNotOnTheConsole()
    {
        var lines = new HandshakeLine[4];
        int n = LiveHandshakeDirector.PcConsole(1, 0.4f, 5005, "Pixel", 1.9f, 1.5f, lines);
        lines[..n].ToArray().Should().NotContain(l => l.Kind == HandshakeLineKind.Linked);
    }

    [Fact]
    public void TheEnglishFallbackReadsLikeTheLab()
    {
        var text = EnglishHandshakeText.Instance;
        text.Line(new HandshakeLine(HandshakeLineKind.Paired, 0, Count: 1)).Should().Be("1 phone paired");
        text.Line(new HandshakeLine(HandshakeLineKind.Paired, 0, Count: 3)).Should().Be("3 phones paired");
        text.Line(new HandshakeLine(HandshakeLineKind.NonePaired, 0)).Should().Be("No phones paired yet");
        text.Line(new HandshakeLine(HandshakeLineKind.Listening, 0, Port: 5005)).Should().Be("Listening on port 5005");
        text.Line(new HandshakeLine(HandshakeLineKind.Listening, 0)).Should().Be("Listening");
        text.Line(new HandshakeLine(HandshakeLineKind.Linked, 0, Name: "Galaxy S26 Ultra")).Should().Be("Galaxy S26 Ultra is linked");
        text.Line(new HandshakeLine(HandshakeLineKind.Opening, 0)).Should().Be("Opening RemEx");
        text.LinkedSuffix.Should().Be("linked");
    }

    private static float? Opt(JsonElement c, string property) =>
        c.GetProperty(property).ValueKind == JsonValueKind.Null ? null : c.GetProperty(property).GetSingle();

    private static JsonDocument Vectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "specs", "live-handshake-director-vectors.json")));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
