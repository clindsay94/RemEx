using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Core.Tests;

/// <summary>
/// Host → client message families reach the Android app instead of being dropped (RemEx-ci98m).
/// </summary>
/// <remarks>
/// <para>
/// **A HOST → CLIENT TYPE THAT IS NOT ROUTED IS DROPPED IN SILENCE ON BOTH SIDES.** The host sends
/// it, the send succeeds, and the phone never hears. That is not a hypothesis: it is how RemEx-y6x6
/// bricked all of v3 file transfer, presenting as "peer did not respond" while the host looked
/// perfectly healthy. It is also why RemEx-hgqs deliberately shipped the clipboard PUSH with no
/// host → client message at all — the reply it wanted would have hit exactly this.
/// </para>
/// <para>
/// **WHAT THIS DOES NOT CLAIM.** It does not enumerate every client-bound <c>MessageTypes</c>
/// constant and demand each be routed here, because that would be false: several travel their own
/// paths entirely (the desktop stream reaches Kotlin through <c>RemexDesktopClient</c>, and the
/// pairing and command replies are correlated by the native client rather than forwarded). A test
/// asserting a rule the code does not follow would be worse than none — someone would "fix" the code
/// to match it. What it pins is narrower and true: the two families that ARE routed by prefix are
/// still routed by prefix.
/// </para>
/// <para>
/// It reads source rather than behaviour because the alternative is a JNI boundary no managed test
/// can cross. That limit is real and worth stating: this proves the forward is present, not that a
/// message survives the whole trip. The trip is proved from a device.
/// </para>
/// </remarks>
public class HostToClientRoutingTests
{
    private static string RouterSource([CallerFilePath] string thisSourceFile = "")
    {
        var path = Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..")),
            "remex.core", "Native", "AndroidNativeExports.cs");

        Assert.True(File.Exists(path), "AndroidNativeExports.cs moved or was renamed");
        return File.ReadAllText(path);
    }

    /// <summary>The body of <c>OnNativeMessageReceived</c>, which is the router.</summary>
    /// <remarks>
    /// Scoped to that method rather than the whole file on purpose. The prefix strings appear in
    /// prose elsewhere in this file — including in the comments explaining why the forwards exist —
    /// so a whole-file search would pass on the documentation alone, with the code deleted.
    /// </remarks>
    private static string RouterBody()
    {
        var source = RouterSource();
        var start = source.IndexOf("private static void OnNativeMessageReceived", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnNativeMessageReceived was renamed - the router has moved");

        // To the next method declaration at the same indentation, which is where the body ends.
        var end = source.IndexOf("\n    private static ", start + 1, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    [Theory]
    [InlineData("file_", "_onFileTransferMessageMethodId")]
    [InlineData("clipboard_", "_onClipboardMessageMethodId")]
    public void TheFamilyIsForwardedByPrefixToItsCallback(string prefix, string callbackField)
    {
        var body = RouterBody();

        Assert.True(
            body.Contains($"StartsWith(\"{prefix}\", StringComparison.Ordinal)", StringComparison.Ordinal),
            $"OnNativeMessageReceived no longer forwards the \"{prefix}\" family by prefix. Every " +
            $"{prefix}* message from the host is now dropped silently - the send succeeds and the " +
            "phone never hears, which is how RemEx-y6x6 bricked v3 file transfer.");

        Assert.True(
            body.Contains(callbackField, StringComparison.Ordinal),
            $"the \"{prefix}\" family is matched but {callbackField} is not used to forward it");
    }

    [Theory]
    [InlineData("clipboard_", "clipboard_content")]
    [InlineData("clipboard_", "clipboard_request")]
    [InlineData("file_", "file_transfer_offer")]
    public void TheWireValueStartsWithThePrefixItIsRoutedBy(string prefix, string wireValue)
    {
        // THE TWO HALVES OF THE ROUTING LIVE IN DIFFERENT FILES AND NOTHING TIED THEM TOGETHER.
        // The router matches a literal prefix; MessageTypes defines the literal wire value. Rename
        // ClipboardContent to "clip_content" and the forward stops matching, the message is dropped
        // in silence on the phone, and every assertion above stays green because the router still
        // contains the string it is asked about.
        Assert.StartsWith(prefix, wireValue, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWireValuesAreTheConstantsAndNotJustLiterals()
    {
        // The theory above pins literals; this ties those literals to the constants the code uses,
        // so the pair cannot drift apart without one of the two failing.
        Assert.Equal("clipboard_content", Remex.Core.Messages.MessageTypes.ClipboardContent);
        Assert.Equal("clipboard_request", Remex.Core.Messages.MessageTypes.ClipboardRequest);
    }

    [Theory]
    [InlineData("onClipboardMessage", "_onClipboardMessageMethodId")]
    [InlineData("onFileTransferMessage", "_onFileTransferMessageMethodId")]
    public void TheCallbackIsLookedUpAndAssignedDuringRegistration(string javaMethod, string field)
    {
        // **THE HOLE THE ROUTER SCAN CANNOT SEE, AND IT IS THE BIGGER ONE.** The forward can be
        // perfectly present and still forward nothing: if the method-id lookup or its assignment is
        // dropped, the field stays IntPtr.Zero and NotifyJavaData returns early WITHOUT LOGGING.
        // Host sends, send succeeds, phone never hears - the same silent drop, one layer down, and
        // the all-or-nothing required-check does not cover it either, because that fires when the
        // KOTLIN method is missing, not when the C# lookup is.
        var source = RouterSource();

        Assert.Contains($"GetRequiredCallbackMethodId(env, clazz, \"{javaMethod}\"", source, StringComparison.Ordinal);
        Assert.Contains($"{field} = {javaMethod}MethodId;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EachFamilyForwardsThroughItsOwnCallbackRatherThanTheOthers()
    {
        // Swapping the two method-id arguments between the two NotifyJavaData calls would leave both
        // prefixes present and both fields mentioned, so every substring assertion in this file
        // passes while BOTH families arrive at the wrong Kotlin callback. Pairing prefix to field
        // inside a single statement is what makes the swap visible.
        var body = RouterBody();

        foreach (var (prefix, field) in new[]
                 {
                     ("file_", "_onFileTransferMessageMethodId"),
                     ("clipboard_", "_onClipboardMessageMethodId"),
                 })
        {
            var match = Regex.Match(
                body,
                $@"StartsWith\(""{Regex.Escape(prefix)}"".*?NotifyJavaData\s*\(\s*{Regex.Escape(field)}",
                RegexOptions.Singleline);

            Assert.True(
                match.Success,
                $"the \"{prefix}\" family is not forwarded through {field} - check whether the two " +
                "NotifyJavaData calls have had their callback fields swapped");
        }
    }

    [Fact]
    public void TheTwoPrefixFamiliesAreNotNarrowedToNamedTypes()
    {
        // Scoped to the two PREFIX families, because the router legitimately compares one named
        // type - host_info, which is not a family and has its own callback. A blanket "no named
        // comparisons" rule would be a rule the code does not follow.
        //
        // BOTH COMMENTS IN THE ROUTER SAY "do NOT narrow this back into an explicit type list", and
        // that instruction exists because narrowing it is what caused the outage: the old
        // hand-maintained allowlist knew only the 2.0 types, so every type the 2.1 overhaul added was
        // dropped. An explicit `type == MessageTypes.X` comparison beside these prefixes is the shape
        // of that mistake reappearing.
        var body = RouterBody();

        Assert.DoesNotContain("MessageTypes.FileTransfer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageTypes.ClipboardContent", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanFindsARouterRatherThanPassingOnAnEmptyString()
    {
        // The anti-vacuity check. A renamed method or a moved file makes every assertion above
        // compare against an empty string, and DoesNotContain in particular would pass loudly.
        var body = RouterBody();

        Assert.True(body.Length > 200, "the router body came back suspiciously short - did it move?");
        Assert.Contains("NotifyJavaData", body, StringComparison.Ordinal);
    }
}
