using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Remex.Agent.Handlers;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The launcher allowlist may only be changed from the PC itself.
/// </summary>
/// <remarks>
/// VULN-3 (RemEx-s032.3) hardened LAUNCHAPP so a launch target must match a persisted launcher
/// entry. That mitigation is only worth anything while the list is curated by the person at the PC
/// — and it was not: any PAIRED client could append an arbitrary local path with
/// <c>launcher_add</c>, or replace the entire list in one message with <c>launcher_sync</c>, and
/// then launch it as the always-elevated host. The allowlist was measuring a list the caller could
/// rewrite (RemEx-q6xt).
/// <para>
/// Not exploitable by the shipping Android client, which has no add/remove UI and only ever sends
/// <c>launcher_sync_request</c>. It was a protocol-surface gap, which is exactly the kind that
/// outlives the assumption that no client happens to use it.
/// </para>
/// </remarks>
public class LauncherAllowlistAuthorizationTests
{
    /// <summary>The three message types that MUTATE the persisted allowlist.</summary>
    private static readonly string[] MutatingTypes =
    [
        MessageTypes.LauncherAdd,
        MessageTypes.LauncherRemove,
        MessageTypes.LauncherSync,
    ];

    [Theory]
    [InlineData(MessageTypes.LauncherAdd)]
    [InlineData(MessageTypes.LauncherRemove)]
    [InlineData(MessageTypes.LauncherSync)]
    public void MutatingLauncherMessages_RequireLoopback(string type)
    {
        Assert.True(
            PingPongHandler.RequiresLoopback(type),
            $"{type} rewrites the allowlist that VULN-3's launch check is measured against, so it " +
            "must not be accepted from a remote client merely because that client is paired.");
    }

    /// <summary>
    /// Asking for the list is a read, and it is the only launcher message Android actually sends.
    /// Gating it would break the phone's launcher screen while protecting nothing.
    /// </summary>
    /// <remarks>
    /// Spelled as a literal because there is no <c>MessageTypes</c> constant for it: Android builds
    /// the JSON by hand (<c>AppLauncherViewModel.kt</c>). The host has no case for it either, which
    /// is its own defect and is filed separately — but it must not become a LOOPBACK defect here.
    /// </remarks>
    [Fact]
    public void LauncherSyncRequest_DoesNotRequireLoopback()
    {
        Assert.False(
            PingPongHandler.RequiresLoopback("launcher_sync_request"),
            "launcher_sync_request is a read, and the only launcher message Android sends.");
    }

    /// <summary>
    /// Exactly three types are gated, checked against every message type the protocol declares.
    /// </summary>
    /// <remarks>
    /// The point is the count. Asserting only that the three known types are gated would still pass
    /// if a fourth allowlist-mutating type were added later and left ungated — which is precisely
    /// how the original gap arose, since <c>launcher_sync</c> was added after the others.
    /// </remarks>
    [Fact]
    public void ExactlyTheThreeMutatingTypes_AreGated()
    {
        var declared = typeof(MessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);   // the reflection must actually find the protocol's message types

        var gated = declared.Where(PingPongHandler.RequiresLoopback)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(MutatingTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray(), gated);
    }

    /// <summary>
    /// The predicate must actually be consulted, with the sense of the test the right way round.
    /// </summary>
    /// <remarks>
    /// A guard that is computed and then ignored is not a hypothetical failure in this codebase —
    /// RemEx-mlce is exactly that, a certificate-pin check that computes its pins and returns true
    /// regardless. Every assertion above would still pass if <c>HandleAsync</c> never called
    /// <see cref="PingPongHandler.RequiresLoopback"/>, so the call site is checked too.
    /// </remarks>
    [Fact]
    public void HandleAsync_ConsultsTheGate()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Handlers", "PingPongHandler.cs"));

        Assert.Contains("!isLoopback && RequiresLoopback(message.Type)", source);
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
