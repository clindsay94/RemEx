using System;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS on anything but Windows, with the reason recorded on the
/// skip rather than left for whoever hits it to work out.
/// </summary>
/// <remarks>
/// CLAUDE.md requires this repo to work equally on Windows and CachyOS, but the suite had never been
/// run on Linux and was not clean there: 590 of 598 agent tests passed, and all 8 failures were tests
/// asserting WINDOWS-ONLY PRIMITIVES rather than product defects (RemEx-z17h). Left unmarked, those
/// failures make a Linux run useless — nobody can tell a real regression from the permanent noise, so
/// nobody runs it, which is how the parity rule quietly stops being enforced.
///
/// DELIBERATELY NOT A WEAKENED ASSERTION. The alternative — relaxing the tests so they also pass on
/// Linux — would have meant testing less on Windows, where the code actually runs. Skipping states
/// the truth: this behaviour only exists on one platform.
///
/// No package needed: xUnit honours <see cref="FactAttribute.Skip"/> set from a derived attribute's
/// constructor, so this stays dependency-free.
/// </remarks>
/// <para>
/// THERE IS NO THEORY COUNTERPART, on purpose. Nothing needs one yet, and this repo does not carry
/// speculative code. If one is ever added, know that xUnit v2's TheoryDiscoverer short-circuits on a
/// non-null Skip and emits a SINGLE test case instead of one per <c>[InlineData]</c> row — so the
/// tidy arithmetic that is currently the evidence no coverage was lost (Windows runs 598 with none
/// skipped; Linux reports 590 plus exactly 8 skips) would stop holding, and the equivalence would
/// need checking some other way.
/// </para>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {because}";
    }
}
