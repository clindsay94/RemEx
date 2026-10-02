using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The PC Commands page and the phone's Commands screen show the same two groups, in the same order,
/// under the same names (RemEx-kq10x.3, cohesion spec decision 5), and both hide what the PC's
/// capability probe rules out.
/// </summary>
public sealed class RemoteCommandsParityTests
{
    private static readonly string[] Standard = ["Lock", "SignOut", "Shutdown", "Restart", "Sleep", "Hibernate"];
    private static readonly string[] Forced = ["ForceShutdown", "ForceRestart", "RestartToUefi"];

    /// <summary>The page's Command binding for each phone action name.</summary>
    private static string PcCommandFor(string action) => action switch
    {
        "Lock" => "LockPcCommand",
        "SignOut" => "SignOutPcCommand",
        "Shutdown" => "ShutdownPcCommand",
        "Restart" => "RestartPcCommand",
        "Sleep" => "SleepPcCommand",
        "Hibernate" => "HibernatePcCommand",
        "ForceShutdown" => "ForceShutdownPcCommand",
        "ForceRestart" => "ForceRestartCommand",
        "RestartToUefi" => "RestartToUefiCommand",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    [Fact]
    public void The_pc_page_shows_standard_then_forced_in_the_shared_order()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteView.axaml"));
        var standardAt = markup.IndexOf("Remote_Graceful}", StringComparison.Ordinal);
        var forcedAt = markup.IndexOf("Remote_Forced}", StringComparison.Ordinal);
        standardAt.Should().BeGreaterThan(0);
        forcedAt.Should().BeGreaterThan(standardAt);

        string[] CommandsIn(string section) => Regex.Matches(section, @"Command=""\{Binding (\w+)\}""")
            .Select(m => m.Groups[1].Value).ToArray();

        var standardCommands = CommandsIn(markup[standardAt..forcedAt]);
        var forcedCommands = CommandsIn(markup[forcedAt..]);

        standardCommands.Should().Equal(Standard.Select(PcCommandFor));
        forcedCommands.Should().Equal(Forced.Select(PcCommandFor));
    }

    [Fact]
    public void Every_pc_tile_hides_on_the_capability_probe()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RemoteView.axaml"));
        foreach (var action in Standard.Concat(Forced))
        {
            var command = PcCommandFor(action);
            var expected = action == "RestartToUefi" ? "CanRestartToUefi" : "Can" + action;
            markup.Should().Contain($@"Command=""{{Binding {command}}}"" IsVisible=""{{Binding {expected}}}""",
                $"the {action} tile must hide when the PC cannot do it");
        }
    }

    [Fact]
    public void The_phone_lays_out_the_same_groups_in_the_same_order()
    {
        var kotlin = File.ReadAllText(Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "java",
            "com", "clindsay94", "remex", "ui", "screens", "CommandsLayout.kt"));

        string[] PhoneGroup(string name)
        {
            var match = Regex.Match(kotlin, $@"CommandGroup\.{name} to listOf\(([^)]*)\)");
            match.Success.Should().BeTrue($"CommandsLayout.kt must still declare the {name} group");
            return Regex.Matches(match.Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        }

        PhoneGroup("STANDARD").Should().Equal(Standard);
        PhoneGroup("FORCED").Should().Equal(Forced);
    }

    [Fact]
    public void A_host_that_did_not_probe_shows_everything()
    {
        HostPowerVerbs.IsOffered(null, "HIBERNATE").Should().BeTrue();
    }

    [Fact]
    public void A_verb_the_host_left_out_is_hidden_and_one_it_listed_is_shown()
    {
        string[] advertised = ["SHUTDOWN", "SLEEP"];
        HostPowerVerbs.IsOffered(advertised, "HIBERNATE").Should().BeFalse();
        HostPowerVerbs.IsOffered(advertised, "SLEEP").Should().BeTrue();
        // Ordinal, like the agent's own RoutineStepExecutor check: the wire names are upper case.
        HostPowerVerbs.IsOffered(advertised, "sleep").Should().BeFalse();
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
