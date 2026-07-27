using System.IO;
using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Covers the one decision in <see cref="FileTransferHostException"/>: whether a host reply is fit
/// to show a user.
/// </summary>
/// <remarks>
/// The type is what lets <c>FileTransferViewModel</c> tell "the phone refused this, and said why"
/// apart from "the socket dropped" — two things that previously arrived as the same
/// <see cref="System.Exception"/> and were shown to the user with equal confidence (RemEx-mznc).
/// The catch ORDER that relies on it needs no test: because this type derives from
/// <see cref="IOException"/>, putting the general clause first is compiler error CS0160.
/// </remarks>
public class FileTransferHostExceptionTests
{
    [Fact]
    public void ForHostError_WithAMessage_CarriesItVerbatimForDisplay()
    {
        // The real reply from FileHostHandler.kt that this whole design exists to preserve.
        const string HostReply = "Adding a shared folder must be done on the phone.";

        var thrown = FileTransferHostException.ForHostError(HostReply, "developer context");

        thrown.Should().BeOfType<FileTransferHostException>(
            "a host reply the user can act on must be distinguishable from an internal failure");
        ((FileTransferHostException)thrown).HostMessage.Should().Be(HostReply,
            "the message is shown as-is, so a prefix or any rewording would reach the user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForHostError_WithNothingUsable_FallsBackToAPlainIOException(string? hostMessage)
    {
        var thrown = FileTransferHostException.ForHostError(hostMessage, "Add root failed with an empty host message.");

        thrown.Should().NotBeOfType<FileTransferHostException>(
            "showing a blank status line tells the user nothing at all — the catch site must fall " +
            "through to its localized copy instead");
        thrown.Should().BeOfType<IOException>();
        thrown.Message.Should().Be("Add root failed with an empty host message.",
            "the developer context still has to reach the log even though no user sees it");
    }

    /// <summary>
    /// The empty-string case is not hypothetical, which is why it is asserted above.
    /// </summary>
    /// <remarks>
    /// <c>AddRemoteRootAsync</c> and <c>RemoveRemoteRootAsync</c> test the host reply with
    /// <c>ErrorMessage is string err</c> — and that pattern MATCHES the empty string. Unlike the
    /// root-listing, metadata and volumes paths, neither adds an <c>IsNullOrWhiteSpace</c> guard, so
    /// a host that set the field but left it empty reaches this factory with "". Those two are also
    /// exactly the paths whose replies are worth showing, so the fallback protects the sites that
    /// can least afford a blank message.
    /// </remarks>
    [Fact]
    public void ForHostError_TreatsWhitespaceAsNothingUsable_NotAsAMessage()
    {
        var thrown = FileTransferHostException.ForHostError("\t\n", "context");

        thrown.Should().NotBeOfType<FileTransferHostException>(
            "whitespace would render as an empty status line, which is worse than generic copy");
    }
}
