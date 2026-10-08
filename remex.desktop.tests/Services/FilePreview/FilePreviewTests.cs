using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services.FilePreview;
using Xunit;

namespace Remex.Desktop.Tests.Services.FilePreview;

/// <summary>
/// The preview pipeline under the new File Transfer screen (2026-10-08 redesign): which files get which preview,
/// how bytes become text, how lines are coloured, and how images and live tails are fetched in range reads.
/// </summary>
public class FilePreviewTests
{
    /// <summary>An in-memory file a range reader serves, which a test can grow, shrink or replace.</summary>
    private sealed class FakeFile
    {
        public byte[] Bytes = [];
        public List<(long Offset, int Length, bool FromEnd)> Reads { get; } = [];
        public int? StallAfterReads;

        public Task<FileReadRangeResponse> Read(long offset, int length, bool fromEnd, CancellationToken ct)
        {
            Reads.Add((offset, length, fromEnd));
            var size = Bytes.LongLength;
            var start = fromEnd ? Math.Max(0, size - length) : offset;
            var count = (int)Math.Clamp(size - start, 0, length);
            if (StallAfterReads is { } n && Reads.Count > n) count = 0; // a host that stops sending
            var data = count > 0 ? Bytes[(int)start..(int)(start + count)] : [];
            return Task.FromResult(new FileReadRangeResponse
            {
                RequestId = "r", Offset = start, DataBase64 = Convert.ToBase64String(data),
                FileSize = size, Eof = count > 0 ? start + count >= size : start >= size && StallAfterReads is null,
            });
        }
    }

    // ── Classifier ──

    [Theory]
    [InlineData("photo.JPG", PreviewKind.Image)]
    [InlineData("shot.webp", PreviewKind.Image)]
    [InlineData("IMG_0001.HEIC", PreviewKind.ImageThumbnailOnly)]
    [InlineData("app.log", PreviewKind.Text)]
    [InlineData("settings.json", PreviewKind.Text)]
    [InlineData("Program.cs", PreviewKind.Text)]
    [InlineData("movie.mp4", PreviewKind.None)]
    [InlineData("setup.exe", PreviewKind.None)]
    [InlineData("README", PreviewKind.Sniff)]
    [InlineData("data.unknownext", PreviewKind.Sniff)]
    public void AFileGetsThePreviewItsNameCallsFor(string name, PreviewKind expected)
    {
        PreviewClassifier.Classify(name, isDirectory: false).Should().Be(expected);
    }

    [Fact]
    public void AFolderIsNeverPreviewed_WhateverItIsCalled()
    {
        PreviewClassifier.Classify("photos.jpg", isDirectory: true).Should().Be(PreviewKind.None);
    }

    // ── Decoder ──

    [Fact]
    public void ANulInTheFirstBytes_LooksBinary_ButUtf16TextDoesNot()
    {
        TextPreviewDecoder.LooksBinary("hello\0world"u8).Should().BeTrue();
        TextPreviewDecoder.LooksBinary("plain text"u8).Should().BeFalse();
        TextPreviewDecoder.LooksBinary([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]).Should().BeFalse(
            "UTF-16 has NUL bytes everywhere, and its BOM says so");
    }

    [Fact]
    public void ByteOrderMarks_AreHonouredAndNotShown()
    {
        TextPreviewDecoder.Decode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']).Should().Be("hi");
        TextPreviewDecoder.Decode([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]).Should().Be("hi");
        TextPreviewDecoder.Decode([0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i']).Should().Be("hi");
    }

    [Fact]
    public void ACharacterCutByTheEndOfARead_IsDropped_NotShownAsAReplacementMark()
    {
        var euro = "a€"u8.ToArray(); // € is three bytes
        var cut = euro[..^1];
        TextPreviewDecoder.Decode(cut, endsMidFile: true).Should().Be("a");
        TextPreviewDecoder.Decode(cut, endsMidFile: false).Should().Be("a�",
            "at the real end of a file a broken character is broken, and saying so is honest");
    }

    [Theory]
    [InlineData(new byte[] { 0x61 }, 1)]
    [InlineData(new byte[] { 0x61, 0xC3 }, 1)]             // 2-byte lead, cut
    [InlineData(new byte[] { 0x61, 0xC3, 0xA9 }, 3)]       // é, whole
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 }, 1)]       // € cut after two
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 }, 0)]       // 4-byte emoji cut after three
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, 4)] // whole emoji
    public void CompleteUtf8Length_TrimsOnlyAnUnfinishedSequence(byte[] bytes, int expected)
    {
        TextPreviewDecoder.CompleteUtf8Length(bytes).Should().Be(expected);
    }

    [Fact]
    public void ATailThatStartsMidFile_DropsItsFirstPartialLine()
    {
        TextPreviewDecoder.Decode("ial line\nwhole line\n"u8, startsMidFile: true).Should().Be("whole line\n");
    }

    // ── Tokenizer ──

    [Theory]
    [InlineData("2026-10-08 14:03:22.123 ERROR Something broke", SyntaxKind.LogError)]
    [InlineData("[14:03:22] WARN low disk", SyntaxKind.LogWarning)]
    [InlineData("fail: Remex.Agent.Handlers[0] boom", SyntaxKind.LogError)]
    [InlineData("dbug: Remex.Agent[0] noise", SyntaxKind.LogDebug)]
    [InlineData("All good, NoErrors and failover handled", SyntaxKind.Plain)]
    public void ALogLine_IsColouredByItsLevelWord_AndOnlyByAWholeOne(string line, SyntaxKind expected)
    {
        SyntaxTokenizer.LevelOf(line).Should().Be(expected);
    }

    [Fact]
    public void ALeadingTimestamp_IsColouredSeparately_AndNotMistakenForOne()
    {
        var spans = SyntaxTokenizer.Tokenize("2026-10-08 14:03:22.123 INFO started", SyntaxLanguage.Log);
        spans.Should().ContainSingle(s => s.Kind == SyntaxKind.Timestamp && s.Start == 0 && s.Length == 23);
        SyntaxTokenizer.LeadingTimestampLength("12 apples").Should().Be(0);
        SyntaxTokenizer.LeadingTimestampLength("1. Introduction").Should().Be(0);
    }

    [Fact]
    public void Json_KeysStringsNumbersAndLiterals_AreTold_Apart()
    {
        const string line = "  \"name\": \"Pixel 9\", \"port\": 5005, \"tls\": true";
        var spans = SyntaxTokenizer.Tokenize(line, SyntaxLanguage.Json);
        string Text(SyntaxSpan s) => line.Substring(s.Start, s.Length);
        spans.Where(s => s.Kind == SyntaxKind.Key).Select(Text).Should().Equal("\"name\"", "\"port\"", "\"tls\"");
        spans.Where(s => s.Kind == SyntaxKind.String).Select(Text).Should().Equal("\"Pixel 9\"");
        spans.Where(s => s.Kind == SyntaxKind.Number).Select(Text).Should().Equal("5005");
        spans.Where(s => s.Kind == SyntaxKind.Keyword).Select(Text).Should().Equal("true");
    }

    [Fact]
    public void Code_ACommentMarkerInsideAString_IsNotAComment()
    {
        const string line = "var url = \"http://example\"; // real comment";
        var spans = SyntaxTokenizer.Tokenize(line, SyntaxLanguage.CLike);
        spans.Single(s => s.Kind == SyntaxKind.String).Start.Should().Be(10);
        line[spans.Single(s => s.Kind == SyntaxKind.Comment).Start..].Should().Be("// real comment");
    }

    [Fact]
    public void Xml_TagsAttributesAndComments_AreColoured()
    {
        const string line = "<Button Content=\"Go\" /><!-- note -->";
        var spans = SyntaxTokenizer.Tokenize(line, SyntaxLanguage.Xml);
        spans.Should().Contain(s => s.Kind == SyntaxKind.Key && line.Substring(s.Start, s.Length) == "Button");
        spans.Should().Contain(s => s.Kind == SyntaxKind.String && line.Substring(s.Start, s.Length) == "\"Go\"");
        spans.Should().Contain(s => s.Kind == SyntaxKind.Comment && line.Substring(s.Start, s.Length) == "<!-- note -->");
    }

    [Fact]
    public void Spans_AreInOrder_NonOverlapping_AndInsideTheLine()
    {
        foreach (var (line, lang) in new[]
        {
            ("{\"a\": [1, 2, \"x\\\"y\"], \"b\": null}", SyntaxLanguage.Json),
            ("# comment with \"quote", SyntaxLanguage.Hash),
            ("x = 'unterminated", SyntaxLanguage.Hash),
            ("<a href='x'>t</a>", SyntaxLanguage.Xml),
        })
        {
            var spans = SyntaxTokenizer.Tokenize(line, lang);
            spans.Should().NotBeEmpty(line); // anti-vacuity
            var end = 0;
            foreach (var s in spans)
            {
                s.Start.Should().BeGreaterThanOrEqualTo(end, line);
                (s.Start + s.Length).Should().BeLessThanOrEqualTo(line.Length, line);
                end = s.Start + s.Length;
            }
        }
    }

    // ── Image loader ──

    [Fact]
    public async Task AnImage_IsFetchedWhole_InOneMegabyteReads_WithProgress()
    {
        var file = new FakeFile { Bytes = Enumerable.Range(0, 2_500_000).Select(i => (byte)i).ToArray() };
        var reported = new List<double>();

        var bytes = await ImagePreviewLoader.LoadAsync(file.Read, new SyncProgress(reported.Add), CancellationToken.None);

        bytes.Should().Equal(file.Bytes);
        file.Reads.Select(r => r.Offset).Should().Equal(0L, 1_048_576L, 2_097_152L);
        reported.Last().Should().Be(1);
    }

    [Fact]
    public async Task AnImageOverTheLimit_IsRefusedAfterOneRead()
    {
        var file = new FakeFile { Bytes = new byte[3_000_000] };

        var act = () => ImagePreviewLoader.LoadAsync(file.Read, null, CancellationToken.None, maxBytes: 2_000_000);

        (await act.Should().ThrowAsync<PreviewTooLargeException>()).Which.FileSize.Should().Be(3_000_000);
        file.Reads.Should().HaveCount(1, "nothing past the first read may be fetched for a file that will be refused");
    }

    [Fact]
    public async Task AHostThatStopsSending_FailsTheImage_RatherThanLoopingForever()
    {
        var file = new FakeFile { Bytes = new byte[2_500_000], StallAfterReads = 1 };

        var act = () => ImagePreviewLoader.LoadAsync(file.Read, null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        file.Reads.Should().HaveCountLessThan(5);
    }

    // ── Text head ──

    [Fact]
    public async Task TextHead_ReadsUpToTheLimit_AndSaysWhenItStopped()
    {
        var file = new FakeFile { Bytes = Encoding.UTF8.GetBytes(new string('x', 3_000_000)) };

        var preview = await TextPreviewLoader.LoadHeadAsync(file.Read, CancellationToken.None);

        preview.IsBinary.Should().BeFalse();
        preview.Truncated.Should().BeTrue();
        preview.Text.Length.Should().Be(FileTransferLimits.PreviewTextMaxBytes);
        preview.FileSize.Should().Be(3_000_000);
    }

    [Fact]
    public async Task ABinaryFileWithATextName_CostsOneRead_AndIsSaidToBeBinary()
    {
        var file = new FakeFile { Bytes = new byte[3_000_000] };

        var preview = await TextPreviewLoader.LoadHeadAsync(file.Read, CancellationToken.None);

        preview.IsBinary.Should().BeTrue();
        file.Reads.Should().HaveCount(1);
    }

    // ── Live tail ──

    [Fact]
    public async Task LiveTail_StartsAtTheEnd_ThenAppendsOnlyWhatWasAdded()
    {
        var file = new FakeFile { Bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 100).Select(i => $"line {i}\n"))) };
        var tail = new TextTail(file.Read, tailBytes: 40);

        var start = await tail.StartAsync(CancellationToken.None);
        start.Reset.Should().BeTrue();
        start.Lines.Should().NotBeEmpty().And.EndWith("line 100");
        start.Lines.First().Should().StartWith("line ", "the partial first line of a mid-file tail is dropped");

        file.Bytes = [.. file.Bytes, .. "line 101\nline 1"u8];
        var update = await tail.PollAsync(CancellationToken.None);
        update.Reset.Should().BeFalse();
        update.Lines.Should().Equal("line 101");
        update.PendingLine.Should().Be("line 1", "a line still being written is shown, not held back");

        file.Bytes = [.. file.Bytes, .. "02\n"u8];
        (await tail.PollAsync(CancellationToken.None)).Lines.Should().Equal("line 102");
    }

    [Fact]
    public async Task LiveTail_RestartsWhenTheFileShrinks()
    {
        var file = new FakeFile { Bytes = "old one\nold two\n"u8.ToArray() };
        var tail = new TextTail(file.Read);
        await tail.StartAsync(CancellationToken.None);

        file.Bytes = "new\n"u8.ToArray(); // rotated
        var update = await tail.PollAsync(CancellationToken.None);

        update.Reset.Should().BeTrue();
        update.Lines.Should().Equal("new");
    }

    [Fact]
    public async Task LiveTail_ACharacterSplitAcrossTwoPolls_JoinsUp()
    {
        var file = new FakeFile { Bytes = "a\n"u8.ToArray() };
        var tail = new TextTail(file.Read);
        await tail.StartAsync(CancellationToken.None);
        var euro = "€\n"u8.ToArray();

        file.Bytes = [.. file.Bytes, euro[0], euro[1]];
        (await tail.PollAsync(CancellationToken.None)).PendingLine.Should().BeEmpty("half a character is not shown");

        file.Bytes = [.. file.Bytes, euro[2], euro[3]];
        (await tail.PollAsync(CancellationToken.None)).Lines.Should().Equal("€");
    }

    [Fact]
    public async Task LiveTail_ALineThatNeverEnds_DoesNotGrowWithoutBound()
    {
        var file = new FakeFile { Bytes = "x\n"u8.ToArray() };
        var tail = new TextTail(file.Read, tailBytes: 64);
        await tail.StartAsync(CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            file.Bytes = [.. file.Bytes, .. Encoding.ASCII.GetBytes(new string('y', 50))];
            var update = await tail.PollAsync(CancellationToken.None);
            update.PendingLine.Length.Should().BeLessThanOrEqualTo(64);
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
