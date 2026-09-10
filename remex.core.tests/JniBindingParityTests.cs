using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that every <c>external fun</c> in <c>RemexCoreClient.kt</c> has a matching
/// <c>[UnmanagedCallersOnly(EntryPoint = …)]</c> in <c>AndroidNativeExports.cs</c>, and vice versa.
/// </summary>
/// <remarks>
/// <para>
/// THE ENTRY-POINT STRING IS THE ONLY THING JOINING THE TWO HALVES, AND NOTHING ELSE CHECKS IT.
/// The C# side already says "entry-point names are the contract"; nothing enforced it. A binding
/// whose name does not match its export compiles on both sides, links on neither, and throws
/// <c>UnsatisfiedLinkError</c> the first time a user presses the button.
/// </para>
/// <para>
/// WHICH IS A SILENT FAILURE HERE, NOT A CRASH, BECAUSE THE KOTLIN WRAPPERS ARE CAREFUL. Each one
/// catches <c>UnsatisfiedLinkError</c> and returns <c>Result.failure</c>; callers then do
/// <c>.getOrNull()</c>. So a mistyped name presents as a control that does nothing at all, with one
/// logcat line and no user-visible error — the exact shape of RemEx-035d6, where the media row was
/// dead for a different reason and looked identical. This guard exists because that change added the
/// eighteenth pair and there was nothing to catch the nineteenth being wrong.
/// </para>
/// <para>
/// It reads the JVM method name from <c>external fun</c> rather than from <c>@JvmName</c>. Those
/// agree everywhere today — if one ever disagrees, this guard is the thing that notices, and the
/// right fix is to teach it about <c>@JvmName</c> rather than to make the names differ.
/// </para>
/// </remarks>
public class JniBindingParityTests
{
    private const string EntryPointPrefix = "Java_com_clindsay94_remex_RemexCoreClient_";

    [Fact]
    public void EveryKotlinExternalFunHasANativeExport()
    {
        var missing = KotlinBindings().Except(NativeExports()).ToList();

        Assert.True(
            missing.Count == 0,
            "Kotlin declares external fun with no matching native export, so calling it throws "
            + $"UnsatisfiedLinkError and the caller sees a control that silently does nothing: {Join(missing)}");
    }

    [Fact]
    public void EveryNativeExportHasAKotlinBinding()
    {
        var unreachable = NativeExports().Except(KotlinBindings()).ToList();

        // The weaker direction, but not a nicety. An export with no binding is either dead weight in
        // a NativeAOT binary or — far likelier — the half that got renamed, in which case the other
        // test names the same pair from the other side and the two together say what happened.
        Assert.True(
            unreachable.Count == 0,
            $"native exports no Kotlin binding can reach: {Join(unreachable)}");
    }

    private static IEnumerable<string> KotlinBindings()
    {
        var source = Strip(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.android", "app", "src", "main", "java", "com", "clindsay94", "remex",
            "RemexCoreClient.kt")));

        return Regex.Matches(source, @"external\s+fun\s+(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
    }

    private static IEnumerable<string> NativeExports()
    {
        var source = Strip(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.core", "Native", "AndroidNativeExports.cs")));

        return Regex.Matches(source, $@"EntryPoint\s*=\s*""{Regex.Escape(EntryPointPrefix)}(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
    }

    /// <summary>
    /// Comments removed, because both files discuss these names at length in prose.
    /// </summary>
    private static string Strip(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*[\s\S]*?\*/", string.Empty), @"//.*", string.Empty);

    private static string Join(IEnumerable<string> names) => string.Join(", ", names.OrderBy(n => n));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
