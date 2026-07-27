using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Every bound property that reads <c>LocalizationService.Instance</c> in its getter must be
/// re-raised when the language changes, and its view-model must detach when disposed.
/// </summary>
/// <remarks>
/// This exists because the same defect was fixed four separate times — RemEx-6h3q (two Remote
/// Desktop capability labels), RemEx-4f30 (<c>ModeLabel</c>/<c>StateLabel</c>), RemEx-si0h
/// (<c>SelectionSummary</c>) and RemEx-4p27 (<c>RemoteRootHint</c>) — and none of those fixes could
/// prevent the next one. The property resolves its text when it is GOT, so switching language
/// changes what it would return while nothing on the view-model has changed; no notification fires
/// and the control keeps rendering the previous language. It is invisible in review precisely
/// because the property is correct in isolation: the defect is the ABSENCE of a subscription
/// somewhere else in the file (RemEx-6ddx).
/// <para>
/// Two real defects were found when this test was written: <c>HostRuntimeSummary</c>, which is
/// re-raised only when host capabilities change and so would hold the old language indefinitely on
/// a stable connection, and <c>ConnectionStatusAccessibleName</c> — a screen-reader name, where
/// stale text is heard rather than seen.
/// </para>
/// </remarks>
public class LocalizedPropertyRefreshTests
{
    private const string Localizer = "LocalizationService.Instance";

    [Fact]
    public void EveryBoundLocalizedProperty_IsRefreshedOnALanguageChange()
    {
        var bound = BoundPropertyNames();
        var offenders = new List<string>();
        var examined = 0;

        foreach (var file in Directory.GetFiles(ViewModelDirectory(), "*.cs"))
        {
            var source = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // The handler's own body PLUS the bodies of same-file methods it calls. One level of
            // indirection is deliberate: FileTransferQueue's handler fans out to the items via
            // RaiseLocalizedLabels(), so a strict "inside the handler" rule would fail correct code.
            var reachable = LocaleHandlerReach(source);
            var subscribes = source.Contains($"{Localizer}.PropertyChanged +=", StringComparison.Ordinal);
            var detaches = source.Contains($"{Localizer}.PropertyChanged -=", StringComparison.Ordinal);

            foreach (var (property, body) in LocalizedStringProperties(source))
            {
                // Unbound properties cannot render stale text. They are a different question -
                // whether they should be bound or deleted - and conflating the two would make this
                // test fail for a reason it does not describe.
                if (!bound.Contains(property))
                    continue;

                examined++;
                var reasons = new List<string>();
                if (!subscribes)
                    reasons.Add($"{name} never subscribes to {Localizer}.PropertyChanged");
                if (!reachable.Contains($"nameof({property})", StringComparison.Ordinal))
                    reasons.Add($"the locale handler never re-raises {property}");
                if (!detaches)
                    reasons.Add($"{name} never detaches with -=, so it is pinned for the process lifetime");

                if (reasons.Count > 0)
                    offenders.Add($"{name}.{property}: {string.Join("; ", reasons)}");
            }
        }

        examined.Should().BeGreaterThan(0,
            "the property/binding scan must match this codebase, or this test passes vacuously");

        offenders.Should().BeEmpty(
            "a bound property that resolves its text from the localizer keeps rendering the previous " +
            "language until some unrelated notification happens to refresh it");
    }

    /// <summary>
    /// View-models that SNAPSHOT localized text into a plain property must recompute it when the
    /// language changes, and the recompute must be reachable from their locale handler.
    /// </summary>
    /// <remarks>
    /// This is the variant the test above cannot see. There, the bound property's getter reads the
    /// localizer, so a scan can find it. Here the bound property is a plain
    /// <c>[ObservableProperty]</c> assigned once from somewhere else - its getter is a field read
    /// with no localizer call anywhere near it - while the user sees exactly the same staleness.
    /// <para>
    /// A general scan for this shape would have to follow a localized value across files and through
    /// property calls, and would need an allowlist to stay quiet - which the test above deliberately
    /// avoids. So this pins the known links instead: cheap, exact, and it fails loudly if someone
    /// deletes the call rather than the defect coming back silently. Extend the table when
    /// RemEx-q3h0's remaining sites are fixed.
    /// </para>
    /// </remarks>
    [Theory]
    // SettingsViewModel used to refresh these only while DISCONNECTED, so a connected user - the
    // normal case - kept the previous language until host capabilities next changed (RemEx-q3h0).
    [InlineData("SettingsViewModel.cs", "UpdateHostCapabilitySummary")]
    // AboutViewModel was already correct and is the reference implementation for the pattern.
    [InlineData("AboutViewModel.cs", "UpdateHostVersion")]
    public void LocaleHandler_RecomputesSnapshottedText(string viewModel, string recomputeMethod)
    {
        var path = Path.Combine(ViewModelDirectory(), viewModel);
        File.Exists(path).Should().BeTrue($"{viewModel} is pinned by this test and must exist");

        var reach = LocaleHandlerReach(File.ReadAllText(path));

        reach.Should().NotBeEmpty($"{viewModel} must subscribe to {Localizer}.PropertyChanged");
        reach.Should().Contain(recomputeMethod,
            $"{viewModel} holds localized text in a plain property, so its locale handler must call " +
            $"{recomputeMethod} to recompute it - otherwise the text keeps the previous language " +
            "until some unrelated event happens to refresh it");
    }

    /// <summary>
    /// Property names bound in XAML, across the three binding syntaxes this project uses.
    /// </summary>
    /// <remarks>
    /// The element form <c>&lt;Binding Path="X"/&gt;</c> is easy to forget and is exactly what the
    /// localized <c>MultiBinding</c>s in RemoteDesktopView use, so omitting it would silently
    /// exempt the properties most likely to be affected.
    /// </remarks>
    private static HashSet<string> BoundPropertyNames()
    {
        var patterns = new[]
        {
            new Regex(@"\{Binding\s+(?:Path=)?([A-Za-z_]\w*)", RegexOptions.Compiled),
            new Regex(@"<Binding\s+Path=""([A-Za-z_]\w*)""", RegexOptions.Compiled),
            new Regex(@"\{CompiledBinding\s+(?:Path=)?([A-Za-z_]\w*)", RegexOptions.Compiled),
        };

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in Directory.GetFiles(ViewDirectory(), "*.axaml"))
        {
            var text = File.ReadAllText(view);
            foreach (var pattern in patterns)
                foreach (Match match in pattern.Matches(text))
                    names.Add(match.Groups[1].Value);
        }
        return names;
    }

    /// <summary>
    /// <c>public string</c> properties whose getter reads the localizer, in both the expression-
    /// bodied and block-bodied forms.
    /// </summary>
    /// <remarks>
    /// METHODS are excluded by construction, and that matters: a method re-reads the localizer on
    /// every call and is correct as written. RemEx-4f30's first scan counted 26 "candidates" of
    /// which only 2 were real, because it matched <c>DescribeFailure</c>, <c>LocalizeKillFailure</c>
    /// and <c>BuildEntries</c>. Requiring <c>=&gt;</c> or <c>{</c> immediately after the name means a
    /// parameter list never matches.
    /// </remarks>
    private static IEnumerable<(string Property, string Body)> LocalizedStringProperties(string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(source, @"public\s+string\??\s+(\w+)\s*=>"))
        {
            var body = ExpressionBody(source, m.Index + m.Length);
            if (seen.Add(m.Groups[1].Value) && body.Contains(Localizer, StringComparison.Ordinal))
                yield return (m.Groups[1].Value, body);
        }

        foreach (Match m in Regex.Matches(source, @"public\s+string\??\s+(\w+)\s*\{"))
        {
            if (!seen.Add(m.Groups[1].Value))
                continue;
            var body = Block(source, source.IndexOf('{', m.Index));
            if (body.Contains(Localizer, StringComparison.Ordinal))
                yield return (m.Groups[1].Value, body);
        }
    }

    /// <summary>Handler body plus the bodies of same-file methods it calls (one level).</summary>
    private static string LocaleHandlerReach(string source)
    {
        var subscription = Regex.Match(source, Localizer.Replace(".", @"\.") + @"\.PropertyChanged\s*\+=\s*(\w+)");
        if (!subscription.Success)
            return string.Empty;

        var reach = MethodBody(source, subscription.Groups[1].Value);
        foreach (Match call in Regex.Matches(reach, @"(\w+)\s*\("))
            reach += MethodBody(source, call.Groups[1].Value);
        return reach;
    }

    /// <summary>Body of a method DECLARED in this file, by name.</summary>
    /// <remarks>
    /// The accessibility prefix is load-bearing, not decoration. Without it the pattern also
    /// matches CALL sites, and <c>Post(() =&gt;</c> parses as a "declaration" of <c>Post</c>
    /// whose body is an unrelated lambda elsewhere in the file. That silently widened the
    /// handler's reach and let the test pass while the recompute call was deleted - found by
    /// injecting exactly that deletion and watching the test stay green.
    /// </remarks>
    private static string MethodBody(string source, string name)
    {
        var m = Regex.Match(
            source,
            @"(?:private|public|internal|protected)[^\n;{}]*\b"
                + Regex.Escape(name) + @"\s*\([^)]*\)\s*(=>|\{)");
        if (!m.Success)
            return string.Empty;
        return m.Groups[1].Value == "{"
            ? Block(source, source.IndexOf('{', m.Index + name.Length))
            : ExpressionBody(source, m.Index + m.Length);
    }

    /// <summary>From an expression body's <c>=&gt;</c> to the <c>;</c> that closes it at depth 0.</summary>
    private static string ExpressionBody(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ';' && depth == 0) return source[start..i];
        }
        return source[start..];
    }

    private static string Block(string source, int open)
    {
        if (open < 0)
            return string.Empty;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        return source[open..];
    }

    private static string ViewModelDirectory() => Path.Combine(RepoRoot(), "remex.desktop", "ViewModels");

    private static string ViewDirectory() => Path.Combine(RepoRoot(), "remex.desktop", "Views");

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }
}
