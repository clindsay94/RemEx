using Avalonia.Controls;
using Avalonia.Input;
using FluentAssertions;
using Remex.Desktop;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-5253o belt-and-braces: whatever path still enters FullScreen (Remote Desktop's immersive
/// mode, an OS shortcut) must leave it with Escape. Exercises the pure logic seam
/// <see cref="MainWindow.ShouldExitFullScreenOnEscape"/> pulled out of the Tunnel-phase KeyDown
/// handler - this project has no Avalonia headless runtime to drive the handler itself.
/// </summary>
public class MainWindowFullScreenEscapeTests
{
    [Fact]
    public void Escape_WhileFullScreen_ShouldExit()
    {
        MainWindow.ShouldExitFullScreenOnEscape(Key.Escape, WindowState.FullScreen)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Minimized)]
    public void Escape_WhileNotFullScreen_ShouldNotExit(WindowState state)
    {
        // Escape must fall through to DismissOverlaysCommand (MainWindow.axaml's KeyBinding) in
        // every other state - this handler is a no-op outside FullScreen.
        MainWindow.ShouldExitFullScreenOnEscape(Key.Escape, state)
            .Should().BeFalse();
    }

    [Fact]
    public void OtherKeys_WhileFullScreen_ShouldNotExit()
    {
        MainWindow.ShouldExitFullScreenOnEscape(Key.Enter, WindowState.FullScreen)
            .Should().BeFalse();
    }
}
