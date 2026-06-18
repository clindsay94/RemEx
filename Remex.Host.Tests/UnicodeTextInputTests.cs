using System.Linq;
using Remex.Host.Services.Input;
using Xunit;

namespace Remex.Host.Tests;

public class UnicodeTextInputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildKeyEventGroups_EmptyOrNull_ReturnsNoGroups(string? text)
    {
        Assert.Empty(UnicodeTextInput.BuildKeyEventGroups(text!));
    }

    [Fact]
    public void BuildKeyEventGroups_BmpCharacter_EmitsDownThenUp()
    {
        var groups = UnicodeTextInput.BuildKeyEventGroups("A");

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Length);
        Assert.Equal(new UnicodeKeyEvent('A', false), group[0]);
        Assert.Equal(new UnicodeKeyEvent('A', true), group[1]);
    }

    [Fact]
    public void BuildKeyEventGroups_BmpString_EmitsOneGroupPerCharacterInOrder()
    {
        var groups = UnicodeTextInput.BuildKeyEventGroups("Hi");

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { new UnicodeKeyEvent('H', false), new UnicodeKeyEvent('H', true) }, groups[0]);
        Assert.Equal(new[] { new UnicodeKeyEvent('i', false), new UnicodeKeyEvent('i', true) }, groups[1]);
    }

    [Fact]
    public void BuildKeyEventGroups_SurrogatePair_EmitsBothDownsBeforeBothUps()
    {
        // U+1F600 GRINNING FACE -> surrogate pair high 0xD83D, low 0xDE00.
        const string emoji = "😀";

        var groups = UnicodeTextInput.BuildKeyEventGroups(emoji);

        // A surrogate pair is one code point -> exactly one atomic group of four events.
        var group = Assert.Single(groups);
        Assert.Equal(
            new[]
            {
                new UnicodeKeyEvent(0xD83D, false), // high key-down
                new UnicodeKeyEvent(0xDE00, false), // low  key-down (must follow the high down)
                new UnicodeKeyEvent(0xD83D, true),  // high key-up
                new UnicodeKeyEvent(0xDE00, true),  // low  key-up
            },
            group);

        // Regression guard: the OLD code produced high-down, high-UP, low-down, low-up — the
        // intervening key-up at index 1 broke surrogate composition. The second event must be a
        // key-DOWN.
        Assert.False(group[1].IsKeyUp);
    }

    [Fact]
    public void BuildKeyEventGroups_MixedBmpAndSurrogate_GroupsEachCodePointSeparately()
    {
        // "a" + grinning face + "b"
        var groups = UnicodeTextInput.BuildKeyEventGroups("a😀b");

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Length); // 'a'
        Assert.Equal(4, groups[1].Length); // emoji surrogate pair
        Assert.Equal(2, groups[2].Length); // 'b'
        Assert.Equal('a', groups[0][0].ScanCode);
        Assert.Equal('b', groups[2][0].ScanCode);
    }

    [Fact]
    public void BuildKeyEventGroups_LoneHighSurrogate_TypedBestEffortAsSingleCodeUnit()
    {
        // Malformed: a high surrogate not followed by a low surrogate. Should not throw or consume
        // a following non-surrogate as if it were the pair's partner.
        var groups = UnicodeTextInput.BuildKeyEventGroups("\uD83DZ");

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Length);
        Assert.Equal(0xD83D, groups[0][0].ScanCode);
        Assert.Equal('Z', groups[1][0].ScanCode);
    }

    [Fact]
    public void BuildKeyEventGroups_AllEvents_AlwaysCarryTheCodeUnitAsScanCode()
    {
        var groups = UnicodeTextInput.BuildKeyEventGroups("😀");

        // Every event within a surrogate group references one of the two code units, never 0.
        Assert.All(groups.SelectMany(g => g), e => Assert.True(e.ScanCode is 0xD83D or 0xDE00));
    }
}
