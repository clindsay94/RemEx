using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the shape of the ffmpeg argument list, and enforces that its numbers are formatted
/// invariantly (RemEx-wssm).
///
/// NOTHING WAS BROKEN WHEN THIS WAS WRITTEN, WHICH IS WHY THE ENFORCEMENT IS A SOURCE TEST. The
/// preceding beads in this series — RemEx-hbma, RemEx-j7el, RemEx-tiih, RemEx-clum — each fixed a
/// real culture defect, and each was provable by injection because a hostile culture changed the
/// emitted string. Here it cannot: every operand this builder emits is structurally non-negative,
/// and a positive integer has no culture-sensitive rendering in .NET, so removing the invariant
/// formatting produces a byte-identical argument list on all 890 cultures. A behavioural test for
/// the routing would be a test that cannot fail.
///
/// So the routing is enforced where it is actually visible — in the source — and the behaviour is
/// pinned separately by the shape assertions below, which is what a mechanical refactor of this kind
/// can really get wrong: a space swallowed at a concatenation join, a flag reordered, the scale
/// filter attached to the wrong codec branch.
/// </summary>
public sealed class FFmpegArgumentFormattingTests
{
    private const string EncoderSource = "Services/RemoteDesktop/FFmpegH264Encoder.cs";

    /// <summary>
    /// The only operands allowed to reach an argument un-formatted, because they are already strings.
    /// </summary>
    /// <remarks>
    /// WHAT THE SCAN BELOW CANNOT SEE, stated here rather than left for someone to assume it is total:
    /// it reads interpolation holes only, so a number concatenated in (<c>Append("-crop " + cropX)</c>)
    /// evades it, and its comment stripping is a regex rather than a lexer, so a literal <c>//</c>
    /// inside an argument string would blank the rest of that line and hide the holes on it. Neither
    /// can happen today — the builder is interpolation-only and its one slash-bearing literal,
    /// <c>/dev/dri/renderD128</c>, is single-slash — but both would be silent, and an enforcement test
    /// that fails silently is worse than none. If either shape ever appears here, this test needs a
    /// real parser, not a wider regex.
    /// </remarks>
    /// <remarks>
    /// AN ALLOW-LIST RATHER THAN A LIST OF KNOWN NUMBERS, deliberately. Naming the nine operands that
    /// exist today would catch a REVERT and nothing else, while the rule this enforces is about the
    /// operand nobody has added yet — a crop origin, an offset, a delta. Inverted, a new
    /// <c>{cropX}</c> fails here the moment it is written. A new genuine string operand fails too, and
    /// is meant to: adding it to this list is the one-line acknowledgement that it carries no number.
    /// </remarks>
    private static readonly string[] StringOperands = ["scaleFilter"];

    [Fact]
    public void NoNumberReachesAnFFmpegArgumentWithoutTheInvariantFormatter()
    {
        // THE ONLY THING THAT CAN CATCH A REVERT HERE. See the class remark: with no negative operand
        // the emitted string is identical either way, so nothing observable changes when the rule is
        // dropped. Reading the source is not a stylistic preference in this one case, it is the only
        // available signal.
        // Newlines normalized because the markers below span lines and File.ReadAllText does not
        // translate them. .gitattributes is `* text=auto eol=lf`, so this file is LF on a fresh
        // checkout and the normalization is a no-op there — it is here for the working copy, which
        // Windows tooling does rewrite with CRLF, and which is exactly how a marker written with \n
        // matched nothing while this test was being written.
        //
        // Both bounds are ASSERTED rather than assumed, which is what turned that into a visible
        // failure instead of a silent one: an IndexOf returning -1 would otherwise have left an empty
        // body to scan, no offenders to find, and a green test that checked nothing at all.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", EncoderSource))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = source.IndexOf("internal static string BuildEncoderArgs(", StringComparison.Ordinal);
        Assert.True(start >= 0, "BuildEncoderArgs was renamed or removed; this test needs updating with it.");
        var end = source.IndexOf("\n    /// <summary>\n    /// One-shot capability probe", start, StringComparison.Ordinal);
        Assert.True(end > start, "The end marker for BuildEncoderArgs moved; this test needs updating with it.");

        // Comments first: several of them name these operands in prose, and a scan that counted those
        // would report offences that do not exist — and, worse, would stay green when the code was
        // reverted but a comment happened to satisfy it. That failure mode has bitten this repo before.
        var body = Regex.Replace(source[start..end], "//[^\n]*", string.Empty);
        Assert.True(body.Length > 1000, $"Only {body.Length} chars of BuildEncoderArgs survived comment "
            + "stripping; the scan below would be looking at almost nothing.");

        var offenders = Regex.Matches(body, @"\{([^{}]+)\}")
            .Select(m => m.Groups[1].Value)
            .Where(hole => !hole.TrimStart().StartsWith("Arg(", StringComparison.Ordinal))
            .Where(hole => !StringOperands.Contains(hole.Trim(), StringComparer.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These ffmpeg argument holes are interpolated directly rather than through Arg(): "
            + string.Join(", ", offenders.Select(o => $"{{{o}}}"))
            + ". If they are numbers, wrap them in Arg() — a positive value renders the same either "
            + "way, so nothing will look broken, but a signed one breaks on 95 locales. If one is "
            + "genuinely a string, add its name to StringOperands.");
    }

    [Theory]
    // The two NVENC paths differ only by the trailing pix_fmt, which is the whole point of having both.
    [InlineData("h264_nvenc_bgra",
        "-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq 24 -b:v 0 -maxrate {R} -bufsize {B} -g {G} -forced-idr 1 -aud 1")]
    [InlineData("h264_nvenc",
        "-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq 24 -b:v 0 -maxrate {R} -bufsize {B} -g {G} -forced-idr 1 -aud 1 -pix_fmt yuv420p")]
    [InlineData("h264_qsv",
        "-c:v h264_qsv -preset veryfast -look_ahead 0 -global_quality 24 -maxrate {R} -bufsize {B} -g {G} -forced-idr 1 -repeat_pps 1 -aud 1")]
    [InlineData("libx264",
        "-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf 24 -maxrate {R} -bufsize {B} -g {G} -x264-params aud=1:repeat-headers=1")]
    public void TheUnscaledArgumentListHasExactlyThisShape(string codec, string codecSegment)
    {
        // WHOLE-STRING, NOT Assert.Contains. The existing FFmpegH264EncoderArgsTests assert individual
        // flags are present, which is the right shape for testing what a flag MEANS but cannot see a
        // swallowed space, a duplicated segment or a filter attached to the wrong branch — exactly what
        // a mechanical rewrite of the interpolations risks.
        //
        // The rate values come from the public compute helpers rather than being frozen as literals, so
        // this stays a test of the ARGUMENT SHAPE. A deliberate change to the bitrate formula should not
        // have to touch it; a change to the argument layout must.
        var expected =
            "-f rawvideo -pix_fmt bgra -s 1920x1080 -r 60 -i - -an "
            + Substitute(codecSegment)
            + " -flush_packets 1 -f h264 -";

        Assert.Equal(expected, FFmpegH264Encoder.BuildEncoderArgs(codec, 1920, 1080, 1920, 1080, 60, 24, forProbe: false));
    }

    [Fact]
    public void TheVaapiArgumentListHasExactlyThisShape()
    {
        // Split out because VA-API is the one branch that does not emit "-vf {filter} ": it opens -vf
        // itself and the scale filter, when present, is appended with a comma. A shared assertion would
        // have had to special-case it and would have stopped pinning the difference.
        var expected =
            "-f rawvideo -pix_fmt bgra -s 1920x1080 -r 60 -i - -an "
            + "-vaapi_device /dev/dri/renderD128 -vf format=nv12,hwupload -c:v h264_vaapi -qp 24 -g "
            + FFmpegH264Encoder.ComputeGop(60) + " -aud 1"
            + " -flush_packets 1 -f h264 -";

        Assert.Equal(expected, FFmpegH264Encoder.BuildEncoderArgs("h264_vaapi", 1920, 1080, 1920, 1080, 60, 24, forProbe: false));
    }

    [Fact]
    public void AScaledCaptureInsertsTheFilterAheadOfTheCodecFlags()
    {
        // The downscale path, which the shape tests above deliberately do not exercise: -s carries the
        // CAPTURE size while the filter carries the ENCODE size, and the filter must be prepended to the
        // codec's own chain rather than emitted as a second -vf that would override it.
        var expected =
            "-f rawvideo -pix_fmt bgra -s 2560x1440 -r 60 -i - -an "
            + "-vf scale=1280:720:flags=bilinear "
            + "-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf 24 -maxrate "
            + FFmpegH264Encoder.ComputeMaxRateBps(1280, 720, 60, 24)
            + " -bufsize " + FFmpegH264Encoder.ComputeBufSizeBps(FFmpegH264Encoder.ComputeMaxRateBps(1280, 720, 60, 24))
            + " -g " + FFmpegH264Encoder.ComputeGop(60)
            + " -x264-params aud=1:repeat-headers=1"
            + " -flush_packets 1 -f h264 -";

        Assert.Equal(expected, FFmpegH264Encoder.BuildEncoderArgs("libx264", 2560, 1440, 1280, 720, 60, 24, forProbe: false));
    }

    [Fact]
    public void AScaledVaapiCaptureAppendsTheFilterWithACommaInsteadOfASecondVf()
    {
        // The branch that is easiest to break silently: here the filter is glued to VA-API's own chain
        // with a comma, so a stray space or a missing comma produces an ffmpeg filter-graph parse error
        // rather than a wrong picture.
        var expected =
            "-f rawvideo -pix_fmt bgra -s 2560x1440 -r 60 -i - -an "
            + "-vaapi_device /dev/dri/renderD128 -vf scale=1280:720:flags=bilinear,"
            + "format=nv12,hwupload -c:v h264_vaapi -qp 24 -g " + FFmpegH264Encoder.ComputeGop(60) + " -aud 1"
            + " -flush_packets 1 -f h264 -";

        Assert.Equal(expected, FFmpegH264Encoder.BuildEncoderArgs("h264_vaapi", 2560, 1440, 1280, 720, 60, 24, forProbe: false));
    }

    [Fact]
    public void TheAmfTargetBitrateIsThreeQuartersOfTheCeiling()
    {
        // AMF is the one branch with arithmetic inside an argument hole, `maxRate * 3 / 4`, so it is the
        // one place the rewrite could have changed a VALUE rather than just its formatting. Integer
        // division, and long rather than int — asserted against the computed ceiling so the relationship
        // is pinned rather than a magic number.
        var maxRate = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, 24);
        var expected =
            "-f rawvideo -pix_fmt bgra -s 1920x1080 -r 60 -i - -an "
            + "-c:v h264_amf -quality speed -rc vbr_peak -b:v " + (maxRate * 3 / 4)
            + " -maxrate " + maxRate
            + " -bufsize " + FFmpegH264Encoder.ComputeBufSizeBps(maxRate)
            + " -g " + FFmpegH264Encoder.ComputeGop(60)
            + " -forced-idr 1 -header_spacing " + FFmpegH264Encoder.ComputeGop(60) + " -aud 1"
            + " -flush_packets 1 -f h264 -";

        Assert.Equal(expected, FFmpegH264Encoder.BuildEncoderArgs("h264_amf", 1920, 1080, 1920, 1080, 60, 24, forProbe: false));
    }

    [Fact]
    public void ProbeModeSwapsOnlyTheTrailingOutputSegment()
    {
        // The probe must open the encoder EXACTLY as the real run does, or its verdict is about a
        // different configuration than the one that will actually stream. Asserted as "identical up to
        // the tail" rather than by repeating the whole expected string a second time.
        const string realTail = " -flush_packets 1 -f h264 -";
        const string probeTail = " -frames:v 1 -f null -";

        var real = FFmpegH264Encoder.BuildEncoderArgs("h264_amf", 1920, 1080, 1920, 1080, 60, 24, forProbe: false);
        var probe = FFmpegH264Encoder.BuildEncoderArgs("h264_amf", 1920, 1080, 1920, 1080, 60, 24, forProbe: true);

        Assert.EndsWith(realTail, real);
        Assert.EndsWith(probeTail, probe);
        Assert.Equal(real[..^realTail.Length], probe[..^probeTail.Length]);
    }

    private static string Substitute(string codecSegment)
    {
        var maxRate = FFmpegH264Encoder.ComputeMaxRateBps(1920, 1080, 60, 24);
        return codecSegment
            .Replace("{R}", maxRate.ToString())
            .Replace("{B}", FFmpegH264Encoder.ComputeBufSizeBps(maxRate).ToString())
            .Replace("{G}", FFmpegH264Encoder.ComputeGop(60).ToString());
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
