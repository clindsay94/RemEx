using System.ComponentModel.DataAnnotations;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// The source-generated validation patterns accept and reject exactly what they used to (RemEx-ygapg).
/// </summary>
/// <remarks>
/// <para>
/// **THE CONVERSION IS THE RISK, NOT THE PATTERNS.** Moving from <c>new Regex(..., Compiled)</c> to
/// <c>[GeneratedRegex]</c> is supposed to be behaviour-preserving, and the reason to do it at all is
/// that <c>Compiled</c> emits IL at runtime — which NativeAOT cannot do, so in the Android core the
/// flag bought nothing and the pattern was interpreted on every call. A conversion that quietly
/// changed what validates would show up as a MAC address the app suddenly refuses, or worse, one it
/// suddenly accepts.
/// </para>
/// <para>
/// Both attributes return <c>Success</c> for null and empty on purpose — presence is
/// <c>[Required]</c>'s job, not theirs — so those cases are pinned here too. Without them a
/// conversion that made the pattern match an empty string would look identical.
/// </para>
/// </remarks>
public class ValidationPatternTests
{
    private static bool Accepts(ValidationAttribute attribute, object? value) =>
        attribute.GetValidationResult(value, new ValidationContext(new object())) == ValidationResult.Success;

    [Theory]
    [InlineData("00:11:22:33:44:55")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("0A:1b:2C:3d:4E:5f")]
    public void AWellFormedMacIsAccepted(string mac) => Assert.True(Accepts(new ValidMacAddressAttribute(), mac));

    [Theory]
    [InlineData("00:11:22:33:44")]            // five groups
    [InlineData("00:11:22:33:44:55:66")]      // seven
    [InlineData("00:11:22:33:44:5")]          // short final group
    [InlineData("00:11:22:33:44:GG")]         // not hex
    [InlineData("001122334455")]              // no separators
    [InlineData(" 00:11:22:33:44:55")]        // leading space - the anchors are what refuse this
    [InlineData("00:11:22:33:44:55 ")]        // trailing space
    public void AMalformedMacIsRejected(string mac) => Assert.False(Accepts(new ValidMacAddressAttribute(), mac));

    [Fact]
    public void ATrailingNewlineIsRejected()
    {
        // **INVERTED RATHER THAN DELETED, BECAUSE ITS FAILURE WAS THE SIGNAL THE FIX HAD LANDED
        // (RemEx-gnkdr).** This pinned the opposite: in .NET `$` matches before a final newline, so an
        // anchored pattern still admitted one trailing \n. RemEx-ygapg left that alone deliberately -
        // it was a behaviour-preserving port - and recorded here that whoever tightened the anchor
        // should expect this test to fail and invert it rather than assume a break. `\z` is that
        // tightening, and this is that inversion.
        //
        // The validator is the layer whose job is to say no. Accepting a malformed value so that
        // PhysicalAddress parsing or URI construction refuses it one layer later does not prevent the
        // failure, it relocates it somewhere with a worse message.
        Assert.False(Accepts(new ValidMacAddressAttribute(), "00:11:22:33:44:55\n"));
        Assert.False(Accepts(new ValidHostnameAttribute(), "example.com\n"));

        // **THE PLATFORM SPLIT IS WHAT MADE THIS MORE THAN A TIDY-UP.** Under `$` the \n was admitted
        // and the leftover \r was not, so the same value authored on Windows was refused while the
        // Linux one sailed through - a platform-dependent answer from a validator. Both are now simply
        // rejected, which is what makes this a simplification rather than a new special case.
        Assert.False(Accepts(new ValidHostnameAttribute(), "example.com\r\n"));

        // ANTI-VACUITY: a pattern that rejected everything would satisfy all of the above.
        Assert.True(Accepts(new ValidMacAddressAttribute(), "00:11:22:33:44:55"));
        Assert.True(Accepts(new ValidHostnameAttribute(), "example.com"));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("sub.example.co.uk")]
    [InlineData("localhost")]
    [InlineData("a")]
    [InlineData("192.168.1.10")]
    public void AWellFormedHostnameIsAccepted(string host) => Assert.True(Accepts(new ValidHostnameAttribute(), host));

    [Theory]
    [InlineData("-example.com")]              // leading hyphen on a label
    [InlineData("example-.com")]              // trailing hyphen
    [InlineData("exa mple.com")]              // space
    [InlineData("example..com")]              // empty label
    [InlineData("example.com-")]              // trailing hyphen on the FINAL label - a separate
                                              // (?<!-) from the one "example-.com" covers, and
                                              // nothing exercised it
    public void AMalformedHostnameIsRejected(string host) => Assert.False(Accepts(new ValidHostnameAttribute(), host));

    [Fact]
    public void ALabelOverSixtyThreeCharactersIsRejectedAndOneAtExactlySixtyThreeIsNot()
    {
        // The boundary the {1,63} carries. Sampled on both sides, because an off-by-one here is
        // invisible to every other case in this file.
        Assert.True(Accepts(new ValidHostnameAttribute(), new string('a', 63) + ".com"));
        Assert.False(Accepts(new ValidHostnameAttribute(), new string('a', 64) + ".com"));
    }

    [Fact]
    public void ANameOverTwoHundredAndFiftyThreeCharactersIsRejected()
    {
        // **EXACT NEIGHBOURS, BECAUSE THE FIRST VERSION SAMPLED 255 AND 194.** Both passed, and so
        // would a pattern mutated to {1,254} - which is precisely the plausible off-by-one here,
        // since 253 vs 254 is the ambiguity in the DNS length rule itself. 63+1+63+1+63+1 is 192, so
        // a final label of 61 makes 253 and 62 makes 254.
        var label = new string('a', 63);
        var prefix = string.Join('.', label, label, label);

        Assert.True(Accepts(new ValidHostnameAttribute(), $"{prefix}.{new string('a', 61)}"));
        Assert.False(Accepts(new ValidHostnameAttribute(), $"{prefix}.{new string('a', 62)}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsenceIsSomebodyElsesProblem(string? value)
    {
        // Both attributes deliberately pass on null/blank so [Required] owns presence. If a
        // conversion made either pattern match an empty string this would be the only thing to
        // notice, since every other case here supplies real input.
        Assert.True(Accepts(new ValidMacAddressAttribute(), value));
        Assert.True(Accepts(new ValidHostnameAttribute(), value));
    }

    [Fact]
    public void SoIsSomethingThatIsNotAStringAtAll()
    {
        // The other half of the same contract, and it had no test: both attributes open with a
        // `value is not string` guard and pass anything else through. That is deliberate - a type
        // mismatch is the model binder's to report, not a MAC validator's - but an unexercised guard
        // is one somebody removes while tidying, and the failure would be a cast exception thrown
        // from validation rather than anything naming the field.
        Assert.True(Accepts(new ValidMacAddressAttribute(), 42));
        Assert.True(Accepts(new ValidHostnameAttribute(), 42));
    }
}
