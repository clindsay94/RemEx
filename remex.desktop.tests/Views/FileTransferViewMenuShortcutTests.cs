using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.Converters;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The File Transfer menus show their shortcuts, Sort shows the current choice, and Esc clears the search box.
/// Source-text checks, like <see cref="FileTransferViewFolderOpenTests"/>: this suite has no headless render.
/// </summary>
public class FileTransferViewMenuShortcutTests
{
    [Theory]
    [InlineData("new KeyGesture(Key.F2)")]
    [InlineData("new KeyGesture(Key.Delete)")]
    [InlineData("new KeyGesture(Key.Space)")]
    [InlineData("new KeyGesture(Key.C, KeyModifiers.Control)")]
    [InlineData("new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift)")]
    [InlineData("new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift)")]
    public void FillMenu_ShowsTheShortcut(string gesture)
    {
        Read("FileTransferView.axaml.cs").Should().Contain(gesture);
    }

    [Fact]
    public void CtrlShiftC_IsMatchedBeforePlainCtrlC()
    {
        var code = Read("FileTransferView.axaml.cs");

        code.IndexOf("Key.C when ctrl && shift", System.StringComparison.Ordinal)
            .Should().BeGreaterThan(0)
            .And.BeLessThan(code.IndexOf("Key.C when ctrl =>", System.StringComparison.Ordinal),
                "otherwise Ctrl+Shift+C would match plain Ctrl+C and copy the files");
    }

    [Theory]
    [InlineData("FileTransfer_SortName", "Name")]
    [InlineData("FileTransfer_SortSize", "Size")]
    [InlineData("FileTransfer_SortDate", "Modified")]
    public void EachSortItem_ChecksItselfWhenItIsTheCurrentSort(string header, string parameter)
    {
        var xaml = Read("FileTransferView.axaml");

        var start = xaml.IndexOf($"Header=\"{{local:Localize {header}}}\"", System.StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var item = xaml.Substring(start, xaml.IndexOf("</MenuItem>", start, System.StringComparison.Ordinal) - start);
        item.Should().Contain("Binding SortField, Converter={x:Static local:StringMatchConverter.EqualsParameter}, ConverterParameter=" + parameter);
    }

    [Fact]
    public void TheParameterConverter_MatchesAnEnumByName()
    {
        StringMatchConverter.EqualsParameter
            .Convert(FileSortField.Size, typeof(bool), "Size", System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(true);
        StringMatchConverter.EqualsParameter
            .Convert(FileSortField.Name, typeof(bool), "Size", System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void EscapeInTheSearchBox_ClearsTheQuery_AndIsHandled()
    {
        var code = Read("FileTransferView.axaml.cs");

        code.Should().Contain("e.Source is TextBox { Name: nameof(SearchBox) } && !string.IsNullOrEmpty(vm.SearchQuery)");
        code.Should().Contain("vm.SearchQuery = string.Empty;");
    }

    private static string Read(string file)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", file));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
