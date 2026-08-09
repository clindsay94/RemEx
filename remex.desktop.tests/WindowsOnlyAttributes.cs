using System;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that SKIPS on anything but Windows, with the reason recorded on the
/// skip rather than left for whoever hits it to work out.
/// </summary>
/// <remarks>
/// DELIBERATELY A SECOND COPY of <c>Remex.Agent.Tests.WindowsOnlyFactAttribute</c> (RemEx-vh62). The
/// bead asked for a decision between moving it to shared test support and duplicating it; this is the
/// duplicate, and the reasoning is that there IS no shared test-support project. Creating one — a
/// csproj, a solution entry and three project references — to hold ten lines with no logic in them
/// would be exactly the speculative infrastructure the original's own doc says this repo does not
/// carry. Drift risk is close to nil: if the two ever disagree, both still skip on non-Windows.
///
/// A LINKED COMPILE ITEM was the other candidate and is genuinely one line —
/// <c>&lt;Compile Include="../remex.agent.tests/WindowsOnlyAttributes.cs" Link="..." /&gt;</c> — with no
/// drift at all. It loses on the namespace: the source declares <c>namespace Remex.Agent.Tests;</c>,
/// so linking it leaks that namespace into this assembly unless both copies are neutralised first,
/// which costs about what the duplication costs. Considered and rejected, not overlooked.
///
/// If a third project ever needs it, that is the moment the shared project earns its place. Keep the
/// two in step until then.
///
/// DELIBERATELY NOT A WEAKENED ASSERTION. The alternative — relaxing the test so it also passes on
/// Linux — would have meant testing less on Windows, where the code actually runs. Skipping states the
/// truth: this behaviour only exists on one platform.
///
/// No package needed: xUnit honours <see cref="FactAttribute.Skip"/> set from a derived attribute's
/// constructor, so this stays dependency-free.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {because}";
    }
}
