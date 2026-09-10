using Remex.Agent.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the default shared roots point at where the folders really are (RemEx-ocl9).
/// </summary>
public class DefaultRootLocationTests
{
    [Theory]
    [InlineData(Environment.SpecialFolder.MyPictures, "Pictures")]
    [InlineData(Environment.SpecialFolder.MyDocuments, "Documents")]
    [InlineData(Environment.SpecialFolder.DesktopDirectory, "Desktop")]
    public void AWellKnownFolderResolvesToWHEREITACTUALLYIS(Environment.SpecialFolder folder, string conventional)
    {
        // THE BUG: these were composed as home/Pictures, which is right only where nothing moved them.
        // OneDrive Known Folder Move - the ordinary consumer setup on Windows - puts them under
        // OneDrive\, and every localised Linux desktop resolves XDG to ~/Bilder or ~/Images. The
        // composed path then either does not exist, so the candidate is dropped and the user gets no
        // such root AT ALL, or it is a stale empty directory the root points into.
        var real = Environment.GetFolderPath(folder);
        var resolved = FileTransferService.SpecialFolderOrDefault(folder, "/nonexistent-home", conventional);

        if (!string.IsNullOrEmpty(real))
        {
            Assert.Equal(real, resolved);
        }
        else
        {
            // The fallback, which only matters where the platform cannot answer at all.
            Assert.Equal(Path.Combine("/nonexistent-home", conventional), resolved);
        }
    }

    [Fact]
    public void AnUnresolvableFolderFallsBackRatherThanReturningARelativePath()
    {
        // GetFolderPath returns "" when it cannot resolve - a stripped profile, or HOME unset under an
        // XDG autostart. Passing that straight through would make Path.Combine produce a RELATIVE
        // root, which resolves against the process working directory: a far worse failure than a
        // missing root, because the user would be browsing somewhere nobody chose.
        // Fed the empty answer directly: an INVALID enum value throws rather than returning "",
        // so there is no folder a test can ask for that reaches this branch through the platform.
        var resolved = FileTransferService.OrConventional("", Path.GetTempPath(), "Pictures");

        Assert.True(Path.IsPathFullyQualified(resolved), $"'{resolved}' is not a full path");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Pictures"), resolved);
    }

    [Fact]
    public void TheHOSTAndTheROOTSAgreeAboutWherePicturesIs()
    {
        // THE CONTRADICTION THAT MADE THIS A BUG RATHER THAN A STYLE POINT: the screenshot folder
        // resolves SpecialFolder.MyPictures while the shared root composed home/Pictures, so one half
        // of the process wrote files somewhere the other half could not see. Screenshots land in a
        // folder the phone cannot reach on exactly the machines where redirection is normal.
        var screenshotSide = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var rootSide = FileTransferService.SpecialFolderOrDefault(
            Environment.SpecialFolder.MyPictures,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Pictures");

        if (!string.IsNullOrEmpty(screenshotSide))
        {
            Assert.Equal(screenshotSide, rootSide);
        }
    }
}
