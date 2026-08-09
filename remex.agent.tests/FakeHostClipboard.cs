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

    public Task<bool> SetTextAsync(string text, CancellationToken ct = default)
    {
        WriteCount++;
        LastText = text;
        LastByteCount = System.Text.Encoding.UTF8.GetByteCount(text);
        return Task.FromResult(Succeeds);
    }
}
