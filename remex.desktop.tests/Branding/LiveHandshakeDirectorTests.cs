using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Remex.Branding;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// The Live Handshake director against the shared vectors (RemEx-8g6n0). The Android director reads
/// the same file, so a rule changed on one platform and not the other fails here or there, never
/// silently. Stepped the way the spec prescribes: <c>now</c> from 0 in 1 ms increments, hand-off
/// expected within 2 ms.
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
        LiveHandshakeDirector.Floor.Should().BeApproximately(k.GetProperty("floor").GetSingle(), 1e-6f);
        LiveHandshakeDirector.Grace.Should().BeApproximately(k.GetProperty("grace").GetSingle(), 1e-6f);
        LiveHandshakeDirector.Cap.Should().BeApproximately(k.GetProperty("cap").GetSingle(), 1e-6f);
        LiveHandshakeDirector.LockHold.Should().BeApproximately(k.GetProperty("lockHold").GetSingle(), 1e-6f);
        LiveHandshakeDirector.Exit.Should().BeApproximately(k.GetProperty("exit").GetSingle(), 1e-6f);
        LiveHandshakeDirector.FadeExit.Should().BeApproximately(k.GetProperty("fadeExit").GetSingle(), 1e-6f);
        LiveHandshakeDirector.FirstPulse.Should().BeApproximately(k.GetProperty("firstPulse").GetSingle(), 1e-6f);
        LiveHandshakeDirector.PulsePeriod.Should().BeApproximately(k.GetProperty("pulsePeriod").GetSingle(), 1e-6f);
    }

    [Fact]
    public void TheVectorFileHasCases()
    {
        CaseNames().Should().HaveCountGreaterThan(10, "the shared vectors must actually be read");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EachVectorHandsOffOnTimeFromTheRightOriginWithTheRightPulses(string name)
    {
        var c = Vectors().RootElement.GetProperty("cases").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == name);

        var timeline = new HandshakeTimeline(
            Peers: c.GetProperty("peers").GetInt32(),
            TargetAt: c.GetProperty("hasTarget").GetBoolean() ? 0f : null,
            ReadyAt: Opt(c, "readyAt"),
            LinkedAt: Opt(c, "linkedAt"),
            FailedAt: Opt(c, "failedAt"),
            SkipAt: Opt(c, "skipAt"),
            ExitFromMark: c.GetProperty("exitFromMark").GetBoolean());
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
        handoff!.Value.Should().BeApproximately(expected, 0.002f, $"'{name}': the hand-off time");

        var origin = LiveHandshakeDirector.Origin(timeline, handoff.Value);
        origin.Should().Be(c.GetProperty("expectOrigin").GetString() == "target" ? HandshakeOrigin.Target : HandshakeOrigin.Mark,
            $"'{name}': where the portal opens");

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
        LiveHandshakeDirector.Handoff(timeline, 2.59f).Should().BeNull();
        LiveHandshakeDirector.Handoff(timeline, 2.60f).Should().BeApproximately(LiveHandshakeDirector.Cap, 1e-4f);
    }

    [Fact]
    public void ReducedMotionFiresNoPulses()
    {
        LiveHandshakeDirector.PulsesStarted(5f, 2.6f, reducedMotion: true).Should().Be(0);
        LiveHandshakeDirector.PulsesStarted(5f, 2.6f, reducedMotion: false).Should().Be(3);
    }

    [Fact]
    public void ThePcStatusLineFollowsTheLabOrder()
    {
        LiveHandshakeDirector.Status(0.1f, 0, 0, null, null, null, null).Kind.Should().Be(HandshakeStatusKind.Starting);
        LiveHandshakeDirector.Status(0.5f, 0, 0, null, null, null, null).Kind.Should().Be(HandshakeStatusKind.NonePaired);

        var listening = LiveHandshakeDirector.Status(0.5f, 3, 0, null, null, 0.32f, 5005);
        listening.Kind.Should().Be(HandshakeStatusKind.Listening);
        listening.Port.Should().Be(5005);
        LiveHandshakeDirector.Status(0.5f, 3, 0, null, null, 0.32f, null).Port.Should().BeNull("an unresolvable port is omitted, not guessed");

        LiveHandshakeDirector.Status(0.1f, 3, 0, null, null, null, null).Kind.Should().Be(HandshakeStatusKind.Starting);
        var pinging = LiveHandshakeDirector.Status(0.5f, 3, 0, null, null, null, null);
        pinging.Kind.Should().Be(HandshakeStatusKind.Pinging);
        pinging.Count.Should().Be(3);

        var some = LiveHandshakeDirector.Status(0.9f, 3, 1, null, null, 0.32f, 5005);
        some.Kind.Should().Be(HandshakeStatusKind.SomeLinked, "once a phone is linked the listener line gives way");
        (some.Count, some.Total).Should().Be((1, 3));

        var locked = LiveHandshakeDirector.Status(0.9f, 3, 1, "Galaxy S26 Ultra", null, 0.32f, 5005);
        locked.Kind.Should().Be(HandshakeStatusKind.LinkedTo);
        locked.Name.Should().Be("Galaxy S26 Ultra");
        locked.Hot.Should().BeTrue();
    }

    [Fact]
    public void TheEnglishFallbackReadsLikeTheLab()
    {
        var text = EnglishHandshakeText.Instance;
        text.Status(new HandshakeStatus(HandshakeStatusKind.Listening, Port: 5005)).Should().Be("LISTENING ON PORT 5005");
        text.Status(new HandshakeStatus(HandshakeStatusKind.Listening)).Should().Be("LISTENING");
        text.Status(new HandshakeStatus(HandshakeStatusKind.SomeLinked, 1, 3)).Should().Be("1 OF 3 LINKED");
        text.Status(new HandshakeStatus(HandshakeStatusKind.LinkedTo, Name: "Galaxy S26 Ultra")).Should().Be("LINKED · GALAXY S26 ULTRA");
        text.Status(new HandshakeStatus(HandshakeStatusKind.Pinging, 1, 1)).Should().Be("PINGING 1 PHONE");
        text.Status(new HandshakeStatus(HandshakeStatusKind.Pinging, 3, 3)).Should().Be("PINGING 3 PHONES");
        text.Status(new HandshakeStatus(HandshakeStatusKind.NonePaired)).Should().Be("NO PHONES PAIRED YET");
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
