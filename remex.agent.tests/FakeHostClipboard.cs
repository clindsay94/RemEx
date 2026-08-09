using Remex.Core.Services.Clipboard;

namespace Remex.Agent.Tests;

/// <summary>
/// An <see cref="IHostClipboard"/> that records instead of writing (RemEx-hgqs).
/// </summary>
/// <remarks>
/// The real implementation needs an Avalonia <c>TopLevel</c> and the UI thread, neither of which
/// exists in a unit test. It also records <see cref="LastByteCount"/> rather than exposing the text
/// by default, so a test that only wants to know "did a write happen, and how big" cannot
/// accidentally start asserting on clipboard content — the payload is the thing this feature is
/// trusted with.
/// </remarks>
public sealed class FakeHostClipboard : IHostClipboard
{
    /// <summary>Set false to simulate a PC with no window available to take the clipboard.</summary>
    public bool Succeeds { get; set; } = true;

    public int WriteCount { get; private set; }

    public int LastByteCount { get; private set; }

    /// <summary>What was written. Exposed for the one test that must prove the text arrives intact.</summary>
    public string? LastText { get; private set; }

    /// <summary>What a read returns. Null means the clipboard could not be read at all.</summary>
    /// <remarks>
    /// Defaults to null rather than empty so a test that forgets to set it exercises the
    /// "could not read" path, which is the one with a distinct user-facing answer. A default of
    /// empty would let a test pass while silently proving nothing about either branch.
    /// </remarks>
    public string? Contents { get; set; }

    public int ReadCount { get; private set; }

    public Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        ReadCount++;
        return Task.FromResult(Contents);
    }

    public Task<bool> SetTextAsync(string text, CancellationToken ct = default)
    {
        WriteCount++;
        LastText = text;
        LastByteCount = System.Text.Encoding.UTF8.GetByteCount(text);
        return Task.FromResult(Succeeds);
    }
}
