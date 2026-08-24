using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-bkmn9. Pins that nothing in the desktop app can produce a
/// <c>FileTransferQueueKind.SendToPhone</c> queue item.
/// </summary>
/// <remarks>
/// <para>
/// The value survives an unusual situation: the button that produced it was removed with
/// RemEx-74kfg, because it never sent anything to a phone — it uploaded into the host's own shared
/// root over loopback, and failed outright when the source file already lived in one. The bead that
/// followed recorded that drag-and-drop still reached the same behaviour through
/// <c>DropZoneResolver</c>'s upper zone, and framed the fix as a product decision about a two-zone
/// drop surface.
/// </para>
/// <para>
/// <b>There was no such surface.</b> <c>DropZoneResolver</c> was written for one that was never
/// built: <c>FileTransferView.OnDrop</c> enqueues <c>Upload</c> unconditionally and never called it,
/// so the class had no caller outside its own tests — which is what made it look alive. It is gone,
/// and the value it resolved to is now unreachable.
/// </para>
/// <para>
/// The value is kept because RemEx-uov9y, the PC-to-phone command channel, is open and is what would
/// give it a real meaning. This guard is what stops it being wired back to the upload path in the
/// meantime, which is the failure it has already had once. <b>When uov9y lands and the value means
/// something, delete this file</b> — it is a hold, not a permanent rule.
/// </para>
/// </remarks>
public class SendToPhoneIsUnreachableTests
{
    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    /// <summary>
    /// A mention of the value that is <b>consuming</b> it: a switch arm, <c>SendToPhone =&gt;</c>.
    /// </summary>
    /// <remarks>
    /// EXEMPTED BY SHAPE, NOT BY FILE, and the difference is the whole guard. An earlier draft
    /// exempted <c>FileTransferViewModel.cs</c> wholesale on the grounds that its only mention was
    /// the <c>ActivityKind</c> arm. That file is also where both producers live —
    /// <c>PickAndEnqueueUploadsAsync</c> and <c>EnqueueUploads</c> — and the exact line RemEx-74kfg
    /// deleted was <c>await PickAndEnqueueUploadsAsync(FileTransferQueueKind.SendToPhone);</c> in it.
    /// Restoring that one line would have left this guard green while re-creating the defect it
    /// names.
    /// </remarks>
    private static readonly Regex ConsumingMention =
        new(@"FileTransferQueueKind\.SendToPhone\s*=>", RegexOptions.Compiled);

    [Fact]
    public void NothingInTheDesktopAppCanProduceASendToPhoneQueueItem()
    {
        var project = Path.Combine(RepoRoot(), "remex.desktop");

        var offenders = Directory
            .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetRelativePath(project, f).Replace('\\', '/'), Text: File.ReadAllText(f)))
            // Split on segments rather than testing for "/obj/": a relative path has no leading
            // separator, so the substring form never matches the two directories it names.
            .Where(f => !f.File.Split('/').Any(s => s is "obj" or "bin"))
            .SelectMany(f => Producers(f.File, f.Text))
            .ToArray();

        offenders.Should().BeEmpty(
            "SendToPhone has nothing behind it until RemEx-uov9y lands — a path that resolves to it "
            + "enqueues an upload into the PC's own shared root and calls it a send to the phone");
    }

    /// <summary>Every mention of the value in one file that is not a switch arm.</summary>
    private static IEnumerable<string> Producers(string file, string text)
    {
        // Comments name it on purpose — this whole situation is explained in one — so strip them
        // before looking, the same way the neighbouring guards do. Replaced with spaces rather than
        // removed so the line numbers still line up with the file on disk.
        var code = Regex.Replace(text, @"/\*.*?\*/", m => Blank(m.Value), RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", m => Blank(m.Value));

        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("FileTransferQueueKind.SendToPhone", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ConsumingMention.IsMatch(lines[i]))
            {
                yield return $"{file}:{i + 1}";
            }
        }
    }

    private static string Blank(string matched) =>
        new string(matched.Select(c => c == '\n' ? '\n' : ' ').ToArray());

    [Fact]
    public void TheFileTransferDropStillEnqueuesAnUpload()
    {
        // The behaviour the deleted resolver would have changed, pinned at the one place that
        // decides it. There is no headless harness here to perform a real drop (RemEx-r8c6).
        var view = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "Views", "FileTransferView.axaml.cs"));

        // Asserted as "calls EnqueueUploads, and does not name SendToPhone" rather than as the exact
        // call text, so dropping the argument to rely on the Upload default - or a formatter wrapping
        // the line - stays green. Both are the same behaviour.
        view.Should().Contain("EnqueueUploads(");
        view.Should().NotContain("SendToPhone");
    }
}
