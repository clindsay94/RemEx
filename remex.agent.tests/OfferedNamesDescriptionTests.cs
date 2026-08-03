using Remex.Agent.Handlers;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins what an incoming-push consent prompt tells the user about the files it authorises
/// (RemEx-7iub).
/// </summary>
/// <remarks>
/// <para>
/// The prompt named the first five files and appended a bare "…", while the grant covered every one
/// of them. A ten-file offer therefore asked somebody to approve five files they could read and five
/// they could not. That became load-bearing once each transfer id was bound to the name it was minted
/// for (RemEx-tutz on the phone): a binding is only as meaningful as what the person was shown.
/// </para>
/// <para>
/// Kept in step with the Kotlin <c>joinOfferedNames</c>, which must produce the same text — the two
/// describe the same protocol to two users, and a prompt that reads differently on each end is a
/// prompt that cannot be reasoned about.
/// </para>
/// </remarks>
public sealed class OfferedNamesDescriptionTests
{
    /// <summary>
    /// The shape the CHANGELOG promises: eight real camera photos, all named.
    /// </summary>
    /// <remarks>
    /// Uses a genuine filename shape (Pixel's `IMG_yyyyMMdd_HHmmss.jpg`, 23 characters) rather than a
    /// short synthetic one. An earlier version of this test used `photo-1.jpg` at 11 characters, which
    /// passed at a budget of 110 and therefore pinned nothing about whether 240 is the right number —
    /// it would have gone green while the promise it exists to protect quietly broke.
    /// </remarks>
    [Fact]
    public void AnOrdinaryEightPhotoShareNamesEveryFile()
    {
        var eight = Enumerable.Range(1, 8).Select(i => $"IMG_20260801_1435{i:D2}.jpg").ToArray();
        Assert.All(eight, n => Assert.Equal(23, n.Length));

        var joined = FileTransferHandler.JoinOfferedNames(eight);

        Assert.All(eight, name => Assert.Contains(name, joined, StringComparison.Ordinal));
        Assert.DoesNotContain('+', joined);
    }

    /// <summary>A blank name is shown as an empty entry, not silently dropped.</summary>
    /// <remarks>
    /// Load-bearing for the cross-platform equivalence: Kotlin's `optString("name")` yields `""` for a
    /// missing or JSON-null name and appends it as an empty entry, so C# must too. It is also the case
    /// a later tidy-up is most likely to "fix" on one side only.
    /// </remarks>
    [Fact]
    public void ABlankNameIsAnEmptyEntryRatherThanADisappearance()
    {
        Assert.Equal("a.txt, , b.txt", FileTransferHandler.JoinOfferedNames(["a.txt", "", "b.txt"]));
        Assert.Equal("a.txt, , b.txt", FileTransferHandler.JoinOfferedNames(["a.txt", null, "b.txt"]));
    }

    [Fact]
    public void AnOfferTooLongToShowStatesHowManyAreHidden()
    {
        var many = Enumerable.Range(1, 40)
            .Select(i => $"a-rather-long-holiday-photo-file-name-{i}.jpeg")
            .ToArray();

        var joined = FileTransferHandler.JoinOfferedNames(many);

        Assert.DoesNotContain('…', joined);

        // The number must be exactly what was left out — a wrong count is a new way of misleading,
        // not an improvement on the ellipsis.
        var marker = joined.LastIndexOf(", +", StringComparison.Ordinal);
        Assert.True(marker > 0, $"expected a trailing count, got: {joined}");
        var claimed = int.Parse(joined[(marker + 3)..]);
        var shown = many.Count(name => joined.Contains(name, StringComparison.Ordinal));
        Assert.Equal(many.Length, shown + claimed);
    }

    [Fact]
    public void ASingleNameIsNeverTruncatedHoweverLong()
    {
        // A half-written name is worse than a long one: it is exactly what the grant binds, and the
        // user cannot tell what they are approving.
        var monster = new string('x', 600) + ".bin";

        Assert.Equal(monster, FileTransferHandler.JoinOfferedNames([monster]));
    }

    /// <summary>
    /// The exact text, so the PC and the phone cannot drift apart silently.
    /// </summary>
    /// <remarks>
    /// `PushConsentRegistryTest.the exact same text as the PC produces` asserts this identical string
    /// from the Kotlin side. Both descriptions are of ONE protocol shown to two people; if they ever
    /// disagree, the prompt stops being something anyone can reason about — and nothing else in
    /// either suite would notice, because each side only ever checks its own output.
    /// </remarks>
    [Theory]
    [InlineData(2, "aaaaaaaaaaaaaaaaaaaaaaaaa, aaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TheExactSameTextAsThePhoneProduces(int count, string expected)
    {
        var names = Enumerable.Repeat(new string('a', 25), count).ToArray();

        Assert.Equal(expected, FileTransferHandler.JoinOfferedNames(names));
    }

    [Fact]
    public void TheOverflowTextIsExactlyWhatThePhoneProduces()
    {
        // Ten 25-character names: nine fit inside the 240-character budget (241 characters once the
        // separators are counted), the tenth does not, and the remainder is stated as a number.
        var names = Enumerable.Repeat(new string('b', 25), 10).ToArray();

        var joined = FileTransferHandler.JoinOfferedNames(names);

        Assert.EndsWith(", +1", joined, StringComparison.Ordinal);
        Assert.Equal(9, joined.Split(", ").Count(part => part.Length == 25));
    }

    /// <summary>
    /// The budget itself, so a change fails HERE with a message naming the other platform.
    /// </summary>
    /// <remarks>
    /// Otherwise a tuned budget surfaces as "expected 9, actual 11" in the overflow test, which tells
    /// the next person nothing about the Kotlin constant they also have to move.
    /// </remarks>
    [Fact]
    public void TheNameBudgetMatchesThePhones()
    {
        var budget = typeof(FileTransferHandler)
            .GetField("OfferedNamesBudget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue();

        Assert.Equal(
            240,
            budget);
    }

    [Fact]
    public void AnEmptyOfferDescribesNothing()
    {
        Assert.Equal(string.Empty, FileTransferHandler.JoinOfferedNames([]));
        Assert.Equal(string.Empty, FileTransferHandler.JoinOfferedNames(null!));
    }
}
