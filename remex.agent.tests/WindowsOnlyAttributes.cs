using System;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS on anything but Windows, with the reason recorded on the
/// skip rather than left for whoever hits it to work out.
/// </summary>
/// <remarks>
/// CLAUDE.md requires this repo to work equally on Windows and CachyOS, but the suite had never been
/// run on Linux and was not clean there: at the time, 590 of 598 agent tests passed and all 8 failures were tests
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
///
/// THERE IS A SECOND COPY in remex.desktop.tests (RemEx-vh62). There is no shared test-support
/// project, and standing one up for ten lines with no logic in them would be the speculative
/// infrastructure this file's own note argues against. If a THIRD project ever needs it, that is
/// when the shared project earns its place. Keep the two in step until then.
/// </remarks>
/// <para>
/// THERE IS NO THEORY COUNTERPART, on purpose. Nothing needs one yet, and this repo does not carry
/// speculative code. If one is ever added, know that xUnit v2's TheoryDiscoverer short-circuits on a
/// non-null Skip and emits a SINGLE test case instead of one per <c>[InlineData]</c> row — so the
/// per-row case count, which is what currently evidences that no coverage was lost, would stop
/// holding and the equivalence would need checking some other way. RemEx-vh62 hit exactly this and
/// split the theory instead. DO NOT WRITE THE COUNTS DOWN HERE: this paragraph used to name them and
/// they were stale within months, which is its own small lesson about suites that grow.
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
