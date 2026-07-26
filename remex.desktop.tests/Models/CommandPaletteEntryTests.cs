using System;
using System.Windows.Input;
using FluentAssertions;
using Remex.Desktop.Models;
using Xunit;

namespace Remex.Desktop.Tests.Models;

/// <summary>
/// Covers <see cref="CommandPaletteEntry"/>'s confirmation contract. The failure direction here is
/// fail-OPEN — supply only some of the three confirm keys and <c>RequiresConfirmation</c> is quietly
/// false, so a destructive entry runs straight from Ctrl+K with no prompt and nothing reports it. The
/// type rejects that at construction (RemEx-eifi); these tests pin that it keeps doing so
/// (RemEx-4b8m).
/// </summary>
public class CommandPaletteEntryTests
{
    private sealed class NoopCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    private static readonly ICommand Command = new NoopCommand();

    [Fact]
    public void NoConfirmKeys_DoesNotRequireConfirmation()
    {
        new CommandPaletteEntry("Go Home", "Navigate", Command)
            .RequiresConfirmation.Should().BeFalse();
    }

    [Fact]
    public void AllThreeConfirmKeys_RequiresConfirmation()
    {
        new CommandPaletteEntry(
                "Sign Out PC", "Power", Command,
                "Confirm_SignOut_Title", "Confirm_SignOut_Message", "Confirm_SignOut_Btn")
            .RequiresConfirmation.Should().BeTrue();
    }

    /// <summary>
    /// Every partial combination must throw rather than silently degrade to "runs immediately". Each
    /// of these is a plausible slip: adding a title and message but forgetting the button key, or
    /// renaming one key and missing a sibling.
    /// </summary>
    [Theory]
    [InlineData("T", null, null)]
    [InlineData(null, "M", null)]
    [InlineData(null, null, "B")]
    [InlineData("T", "M", null)]
    [InlineData("T", null, "B")]
    [InlineData(null, "M", "B")]
    public void PartialConfirmKeys_ThrowAtConstruction(string? title, string? message, string? btn)
    {
        var act = () => new CommandPaletteEntry("Force Shutdown", "Power", Command, title, message, btn);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*must be all set or all null*");
    }

    /// <summary>The message must name the offending entry, or a throw at startup says nothing useful.</summary>
    [Fact]
    public void PartialConfirmKeys_ThrowMentionsTheEntryLabel()
    {
        var act = () => new CommandPaletteEntry("Restart PC", "Power", Command, "Confirm_Restart_Title");

        act.Should().Throw<ArgumentException>().WithMessage("*Restart PC*");
    }
}
