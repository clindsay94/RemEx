using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>Add program dialog: pasted paths, the Save gate and the missing-file hint.</summary>
public sealed class AddProgramViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("remex-addprogram-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeIcons : IIconExtractionService
    {
        public List<string> Asked { get; } = [];

        public string ExtractIconAsBase64(string filePath)
        {
            Asked.Add(filePath);
            return "fake-icon";
        }
    }

    private string MakeProgram(string name = "Fancy Tool.exe")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    [Fact]
    public void AQuotedPathFromExplorerIsStoredUnquotedAndFillsNameAndIcon()
    {
        var path = MakeProgram();
        var icons = new FakeIcons();
        var vm = new AddProgramViewModel(icons);

        vm.TargetPath = $"  \"{path}\"  ";

        vm.TargetPath.Should().Be(path);
        vm.DisplayName.Should().Be("Fancy Tool");
        vm.IconBase64.Should().Be("fake-icon");
        icons.Asked.Should().Contain(path);
    }

    [Fact]
    public async Task ATypedPathKeepsItsSpacesAndQuote_AndIsTrimmedOnlyOnSave()
    {
        var vm = new AddProgramViewModel(new FakeIcons());

        vm.TargetPath = @"C:\Program ";
        vm.TargetPath.Should().Be(@"C:\Program ", "the box writes back every keystroke, so a trim here would eat the space being typed");
        vm.TargetPath = "\"";
        vm.TargetPath.Should().Be("\"", "a lone opening quote is the start of a pasted or typed quoted path");

        var path = MakeProgram();
        vm.TargetPath = path + " ";
        vm.TargetPath.Should().Be(path + " ");
        vm.DisplayName.Should().Be("Fancy Tool", "the file is still recognised through the trailing space");
        vm.PathNotFound.Should().BeFalse();

        AppEntry? saved = null;
        vm.OnSaveRequested = entry => { saved = entry; return Task.CompletedTask; };
        await vm.SaveCommand.ExecuteAsync(null);

        saved!.TargetPath.Should().Be(path);
    }

    [Fact]
    public void SaveWaitsUntilBothThePathAndTheNameAreFilled()
    {
        var vm = new AddProgramViewModel(new FakeIcons());
        vm.SaveCommand.CanExecute(null).Should().BeFalse("nothing is filled in yet");

        vm.DisplayName = "My tool";
        vm.SaveCommand.CanExecute(null).Should().BeFalse("there is still no path");

        vm.TargetPath = Path.Combine(_dir, "missing.exe");
        vm.SaveCommand.CanExecute(null).Should().BeTrue("both fields are set, and a missing file is only a hint");

        vm.DisplayName = "  ";
        vm.SaveCommand.CanExecute(null).Should().BeFalse("a blank name does not count");
    }

    [Fact]
    public void PathNotFoundFlagsAMissingFileOnly()
    {
        var vm = new AddProgramViewModel(new FakeIcons());
        vm.PathNotFound.Should().BeFalse("an empty box is not an error");

        vm.TargetPath = Path.Combine(_dir, "missing.exe");
        vm.PathNotFound.Should().BeTrue();

        vm.TargetPath = MakeProgram();
        vm.PathNotFound.Should().BeFalse();
    }
}
