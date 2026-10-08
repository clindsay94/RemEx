using System.Text;
using System.Text.Json;
using Remex.Core.Helpers;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// The 2026-10-08 file-browser redesign's shared contracts: how a hash is shown and read back, which range
/// reads are valid, and the wire names of the new message pair. The hash vectors are repeated verbatim in
/// the Kotlin twin's <c>HashFormatTest</c>, so both platforms agree on every one.
/// </summary>
public class FileReadRangeAndHashFormatTests
{
    // SHA-256("abc"), the FIPS 180-2 test vector, in each form a person might paste.
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string AbcBase64 = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=";
    // SHA-256("") — a different digest, for the mismatch side.
    private const string EmptyHex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void ToHex_TurnsTheWireBase64IntoLowercaseHex()
    {
        Assert.Equal(AbcHex, HashFormat.ToHex(AbcBase64));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("aGVsbG8=")] // valid Base64, but 5 bytes, not 32
    public void ToHex_OfSomethingThatIsNotASha256_IsNull(string? input)
    {
        Assert.Null(HashFormat.ToHex(input));
    }

    public static TheoryData<string> FormsOfTheSameHash() =>
    [
        AbcHex,
        AbcHex.ToUpperInvariant(),                                   // Get-FileHash prints upper case
        "  " + AbcHex + "\t",                                        // copied with whitespace around it
        string.Join(' ', Enumerable.Range(0, 8).Select(i => AbcHex.Substring(i * 8, 8))), // grouped
        string.Join(':', Enumerable.Range(0, 32).Select(i => AbcHex.Substring(i * 2, 2))), // colon pairs
        AbcBase64,                                                   // what RemEx itself used to show
    ];

    [Theory]
    [MemberData(nameof(FormsOfTheSameHash))]
    public void EveryFormOfAHash_NormalizesToTheSameDigest_AndMatchesTheOthers(string form)
    {
        Assert.True(HashFormat.TryNormalize(form, out var digest));
        Assert.Equal(Convert.FromHexString(AbcHex), digest);
        Assert.True(HashFormat.Matches(form, AbcBase64));
        Assert.True(HashFormat.Matches(AbcHex, form));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ba7816bf")]                     // too short
    [InlineData(AbcHex + "00")]                  // too long
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // not hex, not base64
    [InlineData("md5:900150983cd24fb0d6963f7d28e17f72")]
    public void SomethingThatIsNotASha256_DoesNotNormalize_AndNeverMatches(string input)
    {
        Assert.False(HashFormat.TryNormalize(input, out _));
        Assert.False(HashFormat.Matches(input, input));
    }

    [Fact]
    public void TwoDifferentHashes_DoNotMatch()
    {
        Assert.False(HashFormat.Matches(AbcHex, EmptyHex));
    }

    [Fact]
    public void TheVectorsAreWhatTheyClaimToBe()
    {
        // Anti-vacuity: every assertion above leans on these constants, so prove them.
        var abc = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(AbcHex, Convert.ToHexStringLower(abc));
        Assert.Equal(AbcBase64, Convert.ToBase64String(abc));
        Assert.Equal(EmptyHex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([])));
    }

    // ── Range validation ──

    private static FileReadRangeRequest Range(long offset = 0, int length = 1024, string root = "docs", string path = "a.log") =>
        new() { RequestId = "r", RootId = root, RelativePath = path, Offset = offset, Length = length };

    [Fact]
    public void AnOrdinaryRead_IsValid_UpToTheOneMegabyteCap()
    {
        Assert.True(FileReadRangeValidation.IsValid(Range(), out var error));
        Assert.Null(error);
        Assert.True(FileReadRangeValidation.IsValid(Range(length: FileTransferLimits.ReadRangeMaxBytes), out _));
        Assert.True(FileReadRangeValidation.IsValid(Range(offset: long.MaxValue / 2), out _));
    }

    public static TheoryData<FileReadRangeRequest?> InvalidReads() =>
    [
        null,
        Range(offset: -1),
        Range(length: 0),
        Range(length: -5),
        Range(length: FileTransferLimits.ReadRangeMaxBytes + 1),
        Range(root: ""),
        Range(root: "   "),
        Range(path: ""),
        Range(path: "/"),
    ];

    [Theory]
    [MemberData(nameof(InvalidReads))]
    public void AnInvalidRead_IsRefused_WithAReason(FileReadRangeRequest? request)
    {
        Assert.False(FileReadRangeValidation.IsValid(request, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(100, 10, false, 1000, 100)]   // from an offset: the offset
    [InlineData(0, 256, true, 1000, 744)]     // tail: the last 256 bytes
    [InlineData(0, 4096, true, 1000, 0)]      // tail longer than the file: the whole file
    [InlineData(0, 10, true, 0, 0)]           // tail of an empty file
    public void StartOffset_IsTheOffset_OrTheTail(long offset, int length, bool fromEnd, long size, long expected)
    {
        Assert.Equal(expected, FileReadRangeValidation.StartOffset(offset, length, fromEnd, size));
    }

    [Fact]
    public void AOneMegabyteReply_FitsInsideTheControlMessageLimit()
    {
        // The reason the cap is 1 MiB: base64 grows it by a third, and MessageSerializer refuses anything over
        // 4 MB. If someone raises the cap, this says why it can't go past ~3 MB.
        var reply = new RemexMessage
        {
            Type = MessageTypes.FileReadRangeResponse,
            FileReadRangeResponse = new FileReadRangeResponse
            {
                RequestId = Guid.NewGuid().ToString(),
                DataBase64 = Convert.ToBase64String(new byte[FileTransferLimits.ReadRangeMaxBytes]),
                FileSize = long.MaxValue,
            },
        };
        Assert.True(MessageSerializer.Serialize(reply).Length < 4 * 1024 * 1024);
    }

    // ── Wire shape ──

    [Fact]
    public void TheReadRangePair_RoundTrips_UnderTheNamesTheKotlinHostReads()
    {
        var request = new RemexMessage
        {
            Type = MessageTypes.FileReadRangeRequest,
            FileReadRangeRequest = new FileReadRangeRequest
            {
                RequestId = "r1", RootId = "content://tree/x", RelativePath = "Logs/app.log",
                Offset = 12, Length = 34, FromEnd = true,
            },
        };
        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(request));
        using (var doc = JsonDocument.Parse(json))
        {
            var body = doc.RootElement.GetProperty("fileReadRangeRequest");
            Assert.Equal("file_read_range_request", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal(12, body.GetProperty("offset").GetInt64());
            Assert.Equal(34, body.GetProperty("length").GetInt32());
            Assert.True(body.GetProperty("fromEnd").GetBoolean());
        }

        // The phone writes exactly these keys (FileHostHandler.handleReadRange).
        const string phoneReply = """
            {"type":"file_read_range_response","protocolVersion":3,
             "fileReadRangeResponse":{"requestId":"r1","offset":5,"dataBase64":"aGVsbG8=","fileSize":10,"modifiedUtc":77,"eof":true}}
            """;
        var back = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(phoneReply));
        var body2 = back!.FileReadRangeResponse!;
        Assert.Equal("r1", body2.RequestId);
        Assert.Equal(5, body2.Offset);
        Assert.Equal("hello", Encoding.ASCII.GetString(Convert.FromBase64String(body2.DataBase64!)));
        Assert.Equal(10, body2.FileSize);
        Assert.Equal(77, body2.ModifiedUtc);
        Assert.True(body2.Eof);
    }

    [Fact]
    public void TheNewCapabilities_AreAdditive_AndDefaultToNo()
    {
        const string olderPhone = """{"protocol":3,"binary":true,"resume":true,"ops":[],"pcChanges":false}""";
        var caps = JsonSerializer.Deserialize(olderPhone, Serialization.RemexJsonSerializerContext.Default.FileCapabilities)!;
        Assert.False(caps.ReadRange);
        Assert.False(caps.Hash);

        const string newerPhone = """{"protocol":3,"ops":[],"readRange":true,"hash":true}""";
        var newer = JsonSerializer.Deserialize(newerPhone, Serialization.RemexJsonSerializerContext.Default.FileCapabilities)!;
        Assert.True(newer.ReadRange);
        Assert.True(newer.Hash);
    }
}
