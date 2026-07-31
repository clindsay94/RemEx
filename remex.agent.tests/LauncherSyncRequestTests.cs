using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pull-to-refresh on the phone's App Launcher screen must actually re-read the PC's list.
/// </summary>
/// <remarks>
/// <c>launcher_sync_request</c> existed only as a hand-built JSON literal in
/// <c>AppLauncherViewModel.kt</c>: there was no <c>MessageTypes</c> constant naming it and no case
/// in <see cref="Remex.Agent.Handlers.PingPongHandler"/> answering it, so every Refresh tap fell
/// through to the handler's "Unknown message type" default and was dropped.
/// <para>
/// What hid it for so long is that the host pushes <c>launcher_sync</c> proactively on connect, so
/// the list DID populate — just never because the user asked. The phone then cleared its spinner
/// from a hard-coded 5s safety-net delay, making a dead no-op look like a slow refresh returning
/// unchanged data (RemEx-vpxx).
/// </para>
/// <para>
/// Hence the shape of <see cref="LauncherSyncRequest_ReturnsAListThatChangedSinceConnect"/>: it
/// changes the stored list AFTER the on-connect push, so a reply that merely echoed the connect-time
/// snapshot — or no reply at all — fails. Asserting "a launcher_sync arrives" would pass on the
/// broken build.
/// </para>
/// </remarks>
public class LauncherSyncRequestTests
{
    /// <summary>Storage double whose contents the test can change mid-connection, as the PC's UI would.</summary>
    private sealed class MutableLauncherStorage : ILauncherStorageService
    {
        private volatile List<AppEntry> _entries = [];

        public List<AppEntry> Entries
        {
            get => _entries;
            set => _entries = value;
        }

        // Hand back a copy: the handler forwards what it loads straight into a message, and a test
        // that mutated the same instance afterwards would be proving nothing about the read.
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>(_entries));

        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries)
        {
            _entries = entries.ToList();
            return Task.CompletedTask;
        }
    }

    private static AppEntry Entry(string name, int order) =>
        new(Guid.NewGuid(), name, $"C:\\{name}.exe", "#FF3366", null, order);

    [Fact]
    public async Task LauncherSyncRequest_ReturnsAListThatChangedSinceConnect()
    {
        var storage = new MutableLauncherStorage { Entries = [Entry("Alpha", 0)] };

        using var factory = new RemexHostFactory().WithServices(services =>
            services.AddSingleton<ILauncherStorageService>(storage));

        // Only ever elapses on failure — a working host replies immediately — so it is kept short
        // enough not to stall CI for half a minute when it does.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var ws = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost{RemexConstants.WebSocketPath}"), cts.Token);

        // The proactive on-connect push. Consuming it first is what makes the second sync below
        // unambiguously the answer to the request.
        var onConnect = await ReceiveTypeAsync(ws, MessageTypes.LauncherSync, cts.Token,
            "the host pushes launcher_sync on connect");
        Assert.Equal(["Alpha"], onConnect.LauncherEntries!.Select(e => e.DisplayName));

        // The user adds a program at the PC, then pulls to refresh on the phone.
        storage.Entries = [Entry("Alpha", 0), Entry("Beta", 1)];

        await MessageSerializer.SendAsync(
            ws, new RemexMessage { Type = MessageTypes.LauncherSyncRequest }, cts.Token);

        var refreshed = await ReceiveTypeAsync(ws, MessageTypes.LauncherSync, cts.Token,
            "launcher_sync_request must be answered with the current launcher list");

        Assert.Equal(["Alpha", "Beta"], refreshed.LauncherEntries!.Select(e => e.DisplayName));
    }

    /// <summary>
    /// Reads the socket until <paramref name="type"/> arrives, ignoring telemetry and the other
    /// on-connect syncs. Fails with <paramref name="because"/> rather than hanging or throwing a
    /// bare cancellation when the message never comes — which is the exact failure being guarded.
    /// </summary>
    private static async Task<RemexMessage> ReceiveTypeAsync(
        WebSocket ws, string type, CancellationToken ct, string because)
    {
        try
        {
            while (true)
            {
                var message = await MessageSerializer.ReceiveAsync(ws, ct);
                if (message is null)
                    break;   // socket closed
                if (message.Type == type)
                    return message;
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to the assertion so the failure names the missing message.
        }

        Assert.Fail($"Timed out waiting for a '{type}' message — {because}.");
        return null!;   // unreachable; Assert.Fail always throws
    }

    /// <summary>
    /// Every message type the Android client sends must be declared in <see cref="MessageTypes"/>.
    /// </summary>
    /// <remarks>
    /// This is the generalisation of RemEx-vpxx, and the more valuable half of the fix. Android
    /// builds its JSON by hand in Kotlin and cannot reference the C# constants, so a type can exist
    /// on the phone with no counterpart on the host — and the host's only complaint is one
    /// <c>LogWarning</c> in a <c>default:</c> arm that nobody reads. Worse, RemEx-q6xt's
    /// <c>RequiresLoopback</c> gate matches on CONSTANTS, so a hand-built literal cannot be gated
    /// at all even when it needs to be.
    /// <para>
    /// A missing constant does not prove the type is unhandled, and this test does not claim that —
    /// it pins the weaker property that is nonetheless sufficient to have caught the bug: a type
    /// with no constant cannot be matched by any <c>case</c> or gate, so it cannot be handled.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryMessageTypeAndroidSends_HasAMatchingConstant()
    {
        var androidSources = Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "java");
        Assert.True(Directory.Exists(androidSources),
            $"Android sources not found at '{androidSources}'. If the layout moved, update this path — " +
            "do not delete the test; it is the only thing checking both sides of the wire agree.");

        var declared = typeof(MessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);   // the reflection must actually find the protocol's message types

        // Only literals. `put("type", someVariable)` is a dispatcher whose values are supplied by a
        // caller, so there is no string here to check — those sites are covered by their own tests.
        var literal = new Regex("""put\("type",\s*"(?<type>[a-z0-9_]+)"\s*\)""", RegexOptions.Compiled);

        var undeclared = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(androidSources, "*.kt", SearchOption.AllDirectories))
        {
            foreach (Match match in literal.Matches(File.ReadAllText(file)))
            {
                var type = match.Groups["type"].Value;
                if (!declared.Contains(type))
                    undeclared.TryAdd(type, Path.GetFileName(file));
            }
        }

        Assert.True(undeclared.Count == 0,
            "Android sends message types that Remex.Core does not declare, so no host case or " +
            "loopback gate can match them and they are silently dropped:" +
            string.Concat(undeclared.Select(kv => $"{Environment.NewLine}  '{kv.Key}' ({kv.Value})")));
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
