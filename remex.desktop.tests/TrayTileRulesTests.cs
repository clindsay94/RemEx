using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests;

public class TrayTileRulesTests
{
    [Fact]
    public void Remote_desktop_is_enabled_only_when_a_phone_is_attached()
    {
        Assert.True(TrayTileRules.IsRemoteDesktopEnabled(isPhoneAttached: true));
        Assert.False(TrayTileRules.IsRemoteDesktopEnabled(isPhoneAttached: false));
    }

    [Theory]
    [InlineData(TrayPowerAction.Restart)]
    [InlineData(TrayPowerAction.Shutdown)]
    [InlineData(TrayPowerAction.SignOut)]
    public void Session_ending_actions_require_confirmation(TrayPowerAction action)
    {
        Assert.True(TrayTileRules.RequiresConfirmation(action));
    }

    [Fact]
    public void Hibernate_does_not_require_confirmation()
    {
        // Recoverable: the session comes back exactly as it was. Confirming it would train the
        // user to dismiss the dialog that guards Shutdown.
        Assert.False(TrayTileRules.RequiresConfirmation(TrayPowerAction.Hibernate));
    }

    [Fact]
    public void Every_power_action_has_an_explicit_confirmation_verdict()
    {
        // Guards the default arm: a new enum member must be classified deliberately, not inherit
        // "no confirmation needed" by falling through.
        foreach (TrayPowerAction action in Enum.GetValues<TrayPowerAction>())
        {
            var exception = Record.Exception(() => TrayTileRules.RequiresConfirmation(action));
            Assert.Null(exception);
        }
    }

    [Theory]
    [InlineData(TrayPowerAction.Restart)]
    [InlineData(TrayPowerAction.Shutdown)]
    [InlineData(TrayPowerAction.SignOut)]
    [InlineData(TrayPowerAction.Hibernate)]
    public void Every_power_action_resolves_to_a_real_string(TrayPowerAction action)
    {
        // LocalizationService returns the KEY when a key is missing (LocalizationService.cs:41), so
        // a typo in TrayFlyoutViewModel.PowerLabel puts "Confirm_Restart_Btn" on a menu item and
        // nothing anywhere fails. Resource keys contain an underscore and these four labels do not,
        // which is the cheapest way to tell a resolved string from an unresolved one.
        var label = TrayFlyoutViewModel.PowerLabel(action);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.DoesNotContain('_', label);
    }

    // ---- TrayPowerInvoker: the routing itself, not just the policy ----------------------------
    //
    // These matter more than the policy tests above. "Shutdown requires confirmation" being true
    // is worth nothing if the code path that runs Shutdown never consults it. That is the bug
    // these four catch, and it is silent — the PC just turns off.

    [Fact]
    public async Task Confirmed_destructive_action_executes()
    {
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: _ => Task.FromResult(true),
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.True(result);
        Assert.True(executed);
    }

    [Fact]
    public async Task Declined_destructive_action_does_not_execute()
    {
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: _ => Task.FromResult(false),
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.False(result);
        Assert.False(executed);
    }

    [Fact]
    public async Task Destructive_action_with_no_confirm_delegate_does_not_execute()
    {
        // An unwired view model must DECLINE, not proceed unconfirmed. Same contract as every
        // other destructive command in this app (RemEx-07jx).
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Shutdown,
            confirm: null,
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.False(result);
        Assert.False(executed);
    }

    [Fact]
    public async Task Non_destructive_action_executes_without_asking()
    {
        var asked = false;
        var executed = false;

        var result = await TrayPowerInvoker.InvokeAsync(
            TrayPowerAction.Hibernate,
            confirm: _ => { asked = true; return Task.FromResult(true); },
            execute: _ => { executed = true; return Task.CompletedTask; });

        Assert.True(result);
        Assert.True(executed);
        Assert.False(asked);
    }
}
