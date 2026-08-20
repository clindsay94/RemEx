using System.Linq;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// Path arithmetic for folder transfer (RemEx-q3twg).
///
/// Manifest entries are ROOT-relative because that is what a download request wants, but the LOCAL
/// destination needs the path below the folder the user picked. Getting that subtraction wrong does
/// not throw — it silently writes every file at the wrong depth — so it is pinned here.
/// </summary>
public class RemoteSubtreeTests
{
    private static FileManifestEntry Entry(string relativePath, bool isDirectory = false) => new()
    {
        RelativePath = relativePath,
        IsDirectory = isDirectory,
        SizeBytes = isDirectory ? 0 : 1,
        ModifiedUnixMs = 0,
    };

    [Fact]
    public void ToDestinationRelative_StripsTheEnumeratedFolder()
    {
        var subtree = new RemoteSubtree
        {
            BasePath = "photos/2026",
            Entries = [Entry("photos/2026/june/a.jpg")],
        };

        Assert.Equal("june/a.jpg", subtree.ToDestinationRelative(subtree.Entries[0]));
    }

    [Fact]
    public void ToDestinationRelative_WholeRoot_LeavesThePathAlone()
    {
        var subtree = new RemoteSubtree { BasePath = string.Empty, Entries = [Entry("a/b.txt")] };

        Assert.Equal("a/b.txt", subtree.ToDestinationRelative(subtree.Entries[0]));
    }

    [Fact]
    public void ToDestinationRelative_SiblingWithASharedNamePrefix_IsNotTreatedAsAChild()
    {
        // "photos2" starts with "photos" as a STRING but is a different folder. A prefix test that
        // forgets the separator turns that into a path one character short of correct.
        var subtree = new RemoteSubtree { BasePath = "photos", Entries = [Entry("photos2/a.jpg")] };

        Assert.Equal("photos2/a.jpg", subtree.ToDestinationRelative(subtree.Entries[0]));
    }

    [Fact]
    public void Files_ExcludesDirectories()
    {
        var subtree = new RemoteSubtree
        {
            BasePath = "b",
            Entries = [Entry("b/deep", isDirectory: true), Entry("b/deep/d.txt"), Entry("b/x.txt")],
        };

        Assert.Equal(new[] { "b/deep/d.txt", "b/x.txt" }, subtree.Files.Select(entry => entry.RelativePath));
    }
}
