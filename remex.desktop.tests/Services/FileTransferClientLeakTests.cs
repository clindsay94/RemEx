using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that a download which fails while opening its destination leaves nothing registered behind
/// (RemEx-kdly).
/// </summary>
/// <remarks>
/// <para>
/// <c>DownloadAsync</c> registered its transfer-end waiter, progress reporter and idle-watchdog lease
/// BEFORE opening the destination file, while the try/finally that reaps them started well after. So
/// every failure to open the destination — a read-only target file, a protected folder, a path that
/// no longer exists — stranded all three permanently, once per attempt. Now that those failures have
/// a message a user can act on (RemEx-60li), the natural response is to retry, and every retry leaked
/// again.
/// </para>
/// <para>
/// WHY THIS NEEDED A TEST RATHER THAN JUST A FIX. A leased-but-never-released transfer is invisible
/// from outside: nothing fails, nothing logs, the download reports its error correctly and the user
/// sees exactly what they expect. The only observable is the watchdog's own count, which
/// <c>TransferIdleWatchdog</c> documents as existing for this kind of assertion. That invisibility is
/// how the leak survived a fix whose comment already claimed the invariant — reap on every exit path —
/// while the registrations sat above the guarded region.
/// </para>
/// <para>
/// The disconnected <see cref="ConnectionViewModel"/> is deliberate and sufficient: the failure under
/// test happens while opening a LOCAL file, before any message is sent, so the connection is never
/// reached. That also keeps this clear of the missing test seam recorded in RemEx-wfim, which blocks
/// exercising the parts of this class that do talk to a peer.
/// </para>
/// </remarks>
public class FileTransferClientLeakTests
{
    private static FileTransferClient NewClient() => new(new ConnectionViewModel());

    /// <summary>A path that cannot be opened for writing, because its parent directory does not exist.</summary>
    private static string UnopenablePath() =>
        Path.Combine(Path.GetTempPath(), "remex-kdly-" + Guid.NewGuid().ToString("N"), "nested", "file.bin");

    [Fact]
    public async Task ADownloadThatCannotOpenItsDestinationRegistersNothing()
    {
        // THE BEAD. The count must be back to zero, not merely "the call threw".
        var client = NewClient();
        client.ActiveTransferCount.Should().Be(0, "nothing is in flight before the attempt");
        client.PendingDownloadRegistrationCount.Should().Be(0);

        var attempt = () => client.DownloadAsync(
            "root-1", "remote/file.bin", UnopenablePath(), progress: null, CancellationToken.None);

        await attempt.Should().ThrowAsync<DirectoryNotFoundException>();
        client.ActiveTransferCount.Should().Be(0,
            "a download that never opened its destination must leave no watchdog lease behind");

        // The watchdog is ONE of six registrations. Asserting only on it would stay green while the
        // other five moved back outside the reaping region — five sixths of the leak, invisibly.
        client.PendingDownloadRegistrationCount.Should().Be(0,
            "none of the five download dictionaries may keep an entry for a transfer that never began");
    }

    [Fact]
    public async Task RepeatedFailuresDoNotAccumulate()
    {
        // The realistic shape: the user reads the error, fixes nothing, and tries again. Under the
        // leak this climbed by one every time and never came down for the life of the process.
        var client = NewClient();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var run = () => client.DownloadAsync(
                "root-1", "remote/file.bin", UnopenablePath(), progress: null, CancellationToken.None);
            await run.Should().ThrowAsync<DirectoryNotFoundException>();
        }

        client.ActiveTransferCount.Should().Be(0, "five failed attempts must leave no leases at all");
        client.PendingDownloadRegistrationCount.Should().Be(0, "nor any dictionary entries");
    }

    [Fact]
    public async Task ADownloadOntoADirectoryPathAlsoRegistersNothing()
    {
        // A second, differently-caused failure at the same point, so the fix is not pinned to one
        // way of failing. Opening a directory as a file is UnauthorizedAccessException on Windows and
        // also on Linux, where open() returns EISDIR and .NET maps it to the same type — so this can
        // be asserted strictly on both platforms rather than as "something threw".
        var client = NewClient();
        var directory = Directory.CreateTempSubdirectory("remex-kdly-").FullName;

        try
        {
            var attempt = () => client.DownloadAsync(
                "root-1", "remote/file.bin", directory, progress: null, CancellationToken.None);

            await attempt.Should().ThrowAsync<UnauthorizedAccessException>();
            client.ActiveTransferCount.Should().Be(0);
            client.PendingDownloadRegistrationCount.Should().Be(0);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
