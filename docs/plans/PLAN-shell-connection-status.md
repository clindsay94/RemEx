# Shell Connection Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the nav-drawer connection status from a dot-in-a-pill that looks like a
dead toggle switch into a status button with a tooltip and a details flyout, and make the
File-Sharing Trust card show the device names the user chose.

**Architecture:** Two independent slices in `remex.desktop`, sharing only a commit gate.
Slice A extends `PhonePresence` / `PhonePresenceMonitor` with the state the UI needs
(additively — `IsPhoneAttached` is untouched), adds a pure text-composition helper, then
rebuilds the `ShellView.axaml` control on top. Slice B wires the existing
`PairedDeviceDisplayName.Resolve` into `FileTrustDeviceItem`.

**Tech Stack:** C# / .NET, Avalonia, CommunityToolkit.Mvvm (`[ObservableProperty]`,
`[RelayCommand]`), xUnit + FluentAssertions.

**Spec:** `docs/SPEC-shell-connection-status.md`
**Beads:** `RemEx-44gc6` (Tasks 1–7), `RemEx-9me77` (Task 8)

## Global Constraints

- **Never use `ConfigureAwait(false)` anywhere** (`AGENTS.md:243`).
- Nullable reference types are on in every project. Use `Guard.NotNull(arg)` for argument checks.
- Every user-facing string exists in **all 9** `.resx` files: `Strings.resx` (en), `.es`, `.fr`, `.hi`, `.id`, `.pl`, `.pt-BR`, `.tr`, `.uk`.
- The four PC themes are **CyberNOC, Monolith, SolarFlare, BaseDarkGlass**. Use only `DynamicResource` brushes that already exist in all four. Add no new theme keys.
- **Do not change any version number** — not `<Version>` in `Directory.Build.props`, not Android `versionCode`/`versionName`.
- **Build ONE project per `dotnet build` invocation.** `dotnet build projA projB` fails with `MSBUILD : error MSB1008` and the following `dotnet test --no-build` then silently runs the PREVIOUS assembly and reports a confident green (`bd` memory `a-green-test-run-whose-count-did-not-rise-did-not-run-your-test`).
- **Read the test TOTAL, not just the colour.** A green run whose count did not rise did not run your new tests.
- **Never construct a `git add` path from memory.** Copy the exact case from `git status` output.
- `scripts/verify.ps1` is the only accepted proof the work is finished (`AGENTS.md:96`).

## Existing guard tests this plan must not break

Read these before touching `ShellView.axaml`. They are source-text scanners, so they fail
on the shape of the markup, not on behaviour:

- `remex.desktop.tests/Views/StatusDotPresenceBindingTests.cs:116` — `TheDotsThatWereFixedActuallyBindPresence` requires `ShellView.axaml` to still contain a `status-dot`/`status-ring` element whose text contains `Presence.IsPhoneAttached`. **The redesign keeps the dot as a badge, so this passes — do not delete the dot.**
- Same file, `:50` — `NoStatusDotOutsideTheAllowListStillBindsTheLoopbackLink` fails if any `status-dot` element anywhere under `remex.desktop/` mentions `IsConnected`. The new flyout header dot must bind `Presence.IsPhoneAttached`, never `Connection.IsConnected`.
- Same file, `:106` — `TheAllowListHasNotGrown` pins `DeliberatelyHostLink` at exactly 1 entry. Do not add to it.

---

### Task 1: `PhonePresenceStatus` carries the peer address

**Files:**
- Modify: `remex.desktop/Services/PhonePresence.cs:31-34` (the record), `:58-82` (`Evaluate`)
- Test: `remex.desktop.tests/Services/PhonePresenceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `PhonePresenceStatus.RemoteAddress` (`string?`) — non-null only when exactly one phone is attached and its `RemoteAddress` is non-blank. Task 2 reads it.

**Why a default parameter:** `PhonePresenceStatus` is a positional record struct with ~17
existing tests constructing it as `new(state, count, name)`. Adding a 4th positional
parameter **with a default** keeps every one of those call sites compiling untouched.

- [ ] **Step 0: Record the baseline test total**

```
dotnet test remex.desktop.tests -c Release
```

Write the total down. Task 9 asserts the final total is exactly **42** higher. Without this
number that check is unfalsifiable, and a stale green run is indistinguishable from a real one.

- [ ] **Step 1: Write the failing test**

Append to `remex.desktop.tests/Services/PhonePresenceTests.cs`:

```csharp
    [Fact]
    public void OnePhoneOffersItsAddress()
    {
        var status = PhonePresence.Evaluate(
            [new ClientSession("192.168.1.42", "Galaxy S26")]);

        Assert.Equal("192.168.1.42", status.RemoteAddress);
    }

    [Fact]
    public void SeveralPhonesOfferNoAddress()
    {
        // THE SAME RULE THE NAME ALREADY FOLLOWS (PhonePresence.cs:75-76): with several
        // attached, naming one of them is arbitrary and reads as though it is the only one.
        // An address is worse than a name here - it is the one the user would try to
        // diagnose against.
        var status = PhonePresence.Evaluate(
        [
            new ClientSession("192.168.1.42", "Galaxy S26"),
            new ClientSession("192.168.1.43", "Pixel 9"),
        ]);

        Assert.Null(status.RemoteAddress);
    }

    [Fact]
    public void ALoopbackSessionContributesNoAddress()
    {
        // Loopback is not a phone (PhonePresence.IsPhone), so it must not leak its address
        // into a field the UI presents as "the phone you are talking to".
        var status = PhonePresence.Evaluate([new ClientSession("127.0.0.1", null)]);

        Assert.Null(status.RemoteAddress);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~PhonePresenceTests"
```

Expected: FAIL — `'PhonePresenceStatus' does not contain a definition for 'RemoteAddress'`.

- [ ] **Step 3: Add the field to the record**

In `remex.desktop/Services/PhonePresence.cs`, replace the record declaration at `:31-34`:

```csharp
/// <param name="RemoteAddress">
/// The address of a connected phone when exactly one is attached; otherwise null.
/// </param>
public readonly record struct PhonePresenceStatus(
    PhonePresenceState State,
    int PhoneCount,
    string? FirstDeviceName,
    string? RemoteAddress = null);
```

Keep the three existing `<param>` doc lines above it.

- [ ] **Step 4: Populate it in `Evaluate`**

In the same file, after the existing `var name = ...` block (`:77-79`), add:

```csharp
        // SAME RULE AS THE NAME, one line up, and for a sharper reason: with several phones
        // attached an address is the thing a user would actually try to reach, so offering an
        // arbitrary one is worse than offering none.
        var address = phones.Count == 1 && !string.IsNullOrWhiteSpace(phones[0].RemoteAddress)
            ? phones[0].RemoteAddress
            : null;
```

Then change the return at `:81`:

```csharp
        return new PhonePresenceStatus(state, phones.Count, name, address);
```

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~PhonePresenceTests"
```

Expected: PASS, and the **total is 3 higher** than in Step 2. If the total did not rise, the run is stale.

- [ ] **Step 6: Commit**

```bash
git status --short
git add remex.desktop/Services/PhonePresence.cs remex.desktop.tests/Services/PhonePresenceTests.cs
git commit -m "feat(desktop): PhonePresenceStatus carries the single phone's address (RemEx-44gc6)"
```

---

### Task 2: `ShellConnectionState` and the monitor's new surface

**Files:**
- Modify: `remex.desktop/ViewModels/PhonePresenceMonitor.cs`
- Test: `remex.desktop.tests/ViewModels/ShellConnectionStateTests.cs` (create)

**Interfaces:**
- Consumes: `PhonePresenceStatus.RemoteAddress` (Task 1).
- Produces, all on `PhonePresenceMonitor`:
  - `ShellConnectionState State { get; }` — enum `HostDown | NoPhone | PhoneAttached`
  - `bool IsHostDown { get; }`, `bool HasNoPhone { get; }` — derived, for XAML
  - `string? DeviceName { get; }`, `string? RemoteAddress { get; }`

**Why derived bools:** Avalonia cannot bind an enum to `IsVisible` without a converter, and
`ObjectConverters.Equal` with an enum `ConverterParameter` is awkward in XAML. Two computed
bools are simpler, directly bindable, and independently testable. The enum stays as the
single source of truth; the bools are projections of it.

**Test seam:** `HostDownIsNotAnAbsentPhoneTests.cs` establishes the pattern — assign
`App.EmbeddedHostServices` to a fake `IServiceProvider`, call
`PhonePresenceMonitor.Instance.Refresh()`, assert, and restore in `Dispose`. Reuse it exactly.

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/ViewModels/ShellConnectionStateTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Host-down and no-phone are DIFFERENT states, and the shell can now tell them apart
/// (RemEx-44gc6).
/// </summary>
/// <remarks>
/// IsPhoneAttached is false for both, which is correct and is why it is not enough. With the
/// nav drawer collapsed the dot was the entire indicator, so "RemEx is not running on this PC"
/// and "your phone is in the other room" were the same red dot - and the user goes and checks
/// their phone while the fault is here (PhonePresenceMonitor.cs:124-131).
/// </remarks>
public class ShellConnectionStateTests : IDisposable
{
    private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

    public void Dispose()
    {
        App.EmbeddedHostServices = _saved;
        PhonePresenceMonitor.Instance.Refresh();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoHostRegisteredIsHostDown()
    {
        App.EmbeddedHostServices = new Provider(null);

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.HostDown);
        PhonePresenceMonitor.Instance.IsHostDown.Should().BeTrue();
        PhonePresenceMonitor.Instance.HasNoPhone.Should().BeFalse(
            "host-down is its own state, not a flavour of no-phone");
    }

    [Fact]
    public void AHealthyHostWithNothingPairedIsNoPhone()
    {
        App.EmbeddedHostServices = new Provider(new Source([]));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.NoPhone);
        PhonePresenceMonitor.Instance.HasNoPhone.Should().BeTrue();
        PhonePresenceMonitor.Instance.IsHostDown.Should().BeFalse();
    }

    [Fact]
    public void AnAttachedPhoneSurfacesItsNameAndAddress()
    {
        App.EmbeddedHostServices = new Provider(
            new Source([new ClientSession("192.168.1.42", "Galaxy S26")]));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.PhoneAttached);
        PhonePresenceMonitor.Instance.DeviceName.Should().Be("Galaxy S26");
        PhonePresenceMonitor.Instance.RemoteAddress.Should().Be("192.168.1.42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new string[0])]
    public void IsPhoneAttachedNeverDisagreesWithState(string[]? addresses)
    {
        // THE DRIFT GUARD, and it is the reason this file exists rather than three loose asserts.
        // RemEx-7zzw was filed because five status indicators disagreed screen to screen. Home,
        // Settings, Canvas and the tray still bind IsPhoneAttached; the shell now binds State. If
        // these two can ever disagree, the app contradicts itself again - and the user reads that
        // as a bug in the NEW indicator.
        App.EmbeddedHostServices = addresses is null
            ? new Provider(null)
            : new Provider(new Source([.. Array.ConvertAll(addresses, a => new ClientSession(a, null))]));

        PhonePresenceMonitor.Instance.Refresh();

        var monitor = PhonePresenceMonitor.Instance;
        monitor.IsPhoneAttached.Should().Be(
            monitor.State == ShellConnectionState.PhoneAttached,
            "IsPhoneAttached must be exactly 'State is PhoneAttached' - four other indicators "
            + "still bind it");
    }

    [Fact]
    public void AnAttachedPhoneAgreesWithIsPhoneAttached()
    {
        App.EmbeddedHostServices = new Provider(
            new Source([new ClientSession("10.0.0.5", "Pixel 9")]));

        PhonePresenceMonitor.Instance.Refresh();

        var monitor = PhonePresenceMonitor.Instance;
        monitor.State.Should().Be(ShellConnectionState.PhoneAttached);
        monitor.IsPhoneAttached.Should().BeTrue();
    }

    private sealed class Source(IReadOnlyList<ClientSession> sessions) : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => sessions;
    }

    private sealed class Provider(IClientSessionSource? source) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IClientSessionSource) ? source : null;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellConnectionStateTests"
```

Expected: FAIL — `The type or namespace name 'ShellConnectionState' could not be found`.

- [ ] **Step 3: Add the enum**

In `remex.desktop/ViewModels/PhonePresenceMonitor.cs`, above the class declaration:

```csharp
/// <summary>What the shell's connection status control is reporting.</summary>
/// <remarks>
/// A REFINEMENT OF <see cref="PhonePresenceMonitor.IsPhoneAttached"/>, NOT A REPLACEMENT. That
/// bool is false for both <see cref="HostDown"/> and <see cref="NoPhone"/>, which is correct —
/// no phone is attached in either — and is exactly why it cannot drive a control that has to
/// tell the user WHICH of the two it is. Home, Settings, Canvas and the tray flyout keep
/// binding the bool; only the shell needs this (RemEx-7zzw: five indicators that disagree are
/// worse than five that are uniformly coarse).
/// </remarks>
public enum ShellConnectionState
{
    /// <summary>No <c>IClientSessionSource</c> is registered — the embedded host failed to start.</summary>
    HostDown,

    /// <summary>The host is healthy and no phone is attached.</summary>
    NoPhone,

    /// <summary>At least one phone is attached.</summary>
    PhoneAttached
}
```

- [ ] **Step 4: Add the observable properties**

Inside `PhonePresenceMonitor`, after the `_presenceAccessibleName` field (`:70`):

```csharp
    /// <summary>Which of the three situations the shell control is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostDown))]
    [NotifyPropertyChangedFor(nameof(HasNoPhone))]
    private ShellConnectionState _state;

    /// <summary>The attached phone's name when exactly one is attached and it named itself.</summary>
    [ObservableProperty]
    private string? _deviceName;

    /// <summary>The attached phone's address when exactly one is attached.</summary>
    [ObservableProperty]
    private string? _remoteAddress;

    /// <summary>Bindable projection of <see cref="State"/>; Avalonia cannot bind an enum to IsVisible.</summary>
    public bool IsHostDown => State == ShellConnectionState.HostDown;

    /// <summary>Bindable projection of <see cref="State"/>.</summary>
    public bool HasNoPhone => State == ShellConnectionState.NoPhone;
```

- [ ] **Step 5: Set them in `Refresh`**

In the `source is null` branch (`:134-144`), immediately after `IsPhoneAttached = false;`:

```csharp
            State = ShellConnectionState.HostDown;
            DeviceName = null;
            RemoteAddress = null;
```

In the main path, replace the block at `:150-151`:

```csharp
        var attached = status.State != PhonePresenceState.NoPhone;
        IsPhoneAttached = attached;

        // SET FROM THE SAME `attached` THE BOOL IS SET FROM, on the next line, so the two cannot
        // drift. ShellConnectionStateTests pins that they never disagree.
        State = attached ? ShellConnectionState.PhoneAttached : ShellConnectionState.NoPhone;
        DeviceName = status.FirstDeviceName;
        RemoteAddress = status.RemoteAddress;
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellConnectionStateTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 7: Run the neighbouring suites that touch this singleton**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~PhonePresence|FullyQualifiedName~HostDownIsNotAnAbsentPhone"
```

Expected: PASS. `PhonePresenceMonitor.Instance` is process-wide and these tests mutate
`App.EmbeddedHostServices`, so a leak in `Dispose` shows up here first.

- [ ] **Step 8: Commit**

```bash
git status --short
git add remex.desktop/ViewModels/PhonePresenceMonitor.cs remex.desktop.tests/ViewModels/ShellConnectionStateTests.cs
git commit -m "feat(desktop): shell presence monitor distinguishes host-down from no-phone (RemEx-44gc6)"
```

---

### Task 3: The tooltip text, composed purely

**Files:**
- Create: `remex.desktop/Services/ShellStatusText.cs`
- Test: `remex.desktop.tests/Services/ShellStatusTextTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `static string ShellStatusText.Tooltip(string? presence, string? hostLink, string? latency)` — Task 5 calls it from `ShellViewModel`.

**Why a pure helper and not a property on the monitor:** the tooltip needs the host link and
latency, which live on `ConnectionViewModel`, not on `PhonePresenceMonitor`. The monitor owns
presence and must not grow a dependency on the connection. This follows the split
`PhonePresence.Describe` already uses (`PhonePresence.cs:127-131`): **the decision is pure,
only the lookup is not.**

> **Spec correction:** `SPEC-shell-connection-status.md` §2 lists `SummaryTooltip` as a
> property on `PhonePresenceMonitor`. It cannot be — the monitor has no access to
> `Connection.*`. The composition lives here and `ShellViewModel` exposes it. Update the spec
> in Task 9.

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/Services/ShellStatusTextTests.cs`:

```csharp
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The one line the COLLAPSED nav drawer can show (RemEx-44gc6).
/// </summary>
/// <remarks>
/// With the drawer collapsed every text line is hidden and the control is a dot in a pill, so
/// this tooltip is the entire information channel. Pure, so it is testable without a resource
/// system or a view - the same split PhonePresence.Describe uses.
/// </remarks>
public class ShellStatusTextTests
{
    [Fact]
    public void AllThreePartsAreJoined()
    {
        ShellStatusText.Tooltip("Galaxy S26 connected", "Connected to host", "12 ms")
            .Should().Be("Galaxy S26 connected\nConnected to host\n12 ms");
    }

    [Fact]
    public void AMissingLatencyLeavesNoBlankLine()
    {
        // Latency is only published while connected (ShellView.axaml:251 hides it otherwise), so
        // the disconnected case is the COMMON one - a trailing blank line would be the normal
        // rendering, not the edge case.
        ShellStatusText.Tooltip("No phone connected", "Disconnected", null)
            .Should().Be("No phone connected\nDisconnected");
    }

    [Fact]
    public void AMissingHostLinkStillShowsPresence()
    {
        ShellStatusText.Tooltip("No phone connected", null, null)
            .Should().Be("No phone connected");
    }

    [Fact]
    public void WhitespaceOnlyPartsAreDroppedRatherThanJoined()
    {
        ShellStatusText.Tooltip("Phone connected", "   ", "")
            .Should().Be("Phone connected");
    }

    [Fact]
    public void ABlankPresenceStillReturnsSomethingRatherThanAnEmptyTooltip()
    {
        // An empty tooltip renders as a stray empty popup on hover, which is worse than none at
        // all. Presence is never blank in practice - the monitor always sets it - but a tooltip
        // that can render empty is a defect waiting for a first-tick race.
        ShellStatusText.Tooltip("", "Connected to host", null)
            .Should().Be("Connected to host");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusTextTests"
```

Expected: FAIL — `The type or namespace name 'ShellStatusText' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `remex.desktop/Services/ShellStatusText.cs`:

```csharp
namespace Remex.Desktop.Services;

/// <summary>
/// Composes the shell status control's tooltip (RemEx-44gc6).
/// </summary>
/// <remarks>
/// <para>
/// THE COLLAPSED NAV DRAWER HAS NO OTHER CHANNEL. Every text line in that control is
/// IsVisible-bound to IsDrawerOpen, so collapsed it is a dot in a rounded border — which is
/// both the reported "weird toggle switch" and, functionally, one bit of information.
/// </para>
/// <para>
/// PURE, AND NOT A PROPERTY ON <c>PhonePresenceMonitor</c>. Two of the three parts come from
/// <c>ConnectionViewModel</c>, and the monitor deliberately knows nothing about the connection
/// — it is a process-wide singleton whose whole point is that presence is NOT the host link
/// (RemEx-porg). Keeping the join here follows the split <c>PhonePresence.Describe</c> already
/// uses: the decision is pure, only the lookup is not.
/// </para>
/// </remarks>
public static class ShellStatusText
{
    /// <summary>
    /// Joins the presence line, the host-link line and the latency into one tooltip.
    /// </summary>
    /// <remarks>
    /// BLANK PARTS ARE DROPPED, NOT JOINED. Latency is published only while connected, so a
    /// naive join would put a trailing blank line on the tooltip in the ordinary disconnected
    /// case. An all-blank input returns an empty string, which Avalonia renders as no tooltip
    /// rather than an empty popup.
    /// </remarks>
    public static string Tooltip(string? presence, string? hostLink, string? latency)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(presence)) parts.Add(presence.Trim());
        if (!string.IsNullOrWhiteSpace(hostLink)) parts.Add(hostLink.Trim());
        if (!string.IsNullOrWhiteSpace(latency)) parts.Add(latency.Trim());

        return string.Join('\n', parts);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusTextTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git status --short
git add remex.desktop/Services/ShellStatusText.cs remex.desktop.tests/Services/ShellStatusTextTests.cs
git commit -m "feat(desktop): pure tooltip composition for the shell status control (RemEx-44gc6)"
```

---

### Task 4: Localization — six keys across nine files

**Files:**
- Modify: `remex.desktop/Localization/Strings.resx` and the eight locale files
- Test: `remex.desktop.tests/Services/ShellStatusKeysTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: resource keys `Shell_StatusFlyoutAddress`, `Shell_StatusFlyoutHostLink`, `Shell_StatusFlyoutLatency`, `Shell_StatusFlyoutRuntime`, `Shell_StatusOpenDiagnostics`, `A11y_ConnectionStatusButton`. Tasks 5 and 6 bind them.

Do this **before** the XAML so the markup never references a key that does not exist — a
missing key resolves silently at runtime.

- [ ] **Step 1: Write the failing parity test**

Create `remex.desktop.tests/Services/ShellStatusKeysTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The shell status control's strings exist in every language (RemEx-44gc6).
/// </summary>
/// <remarks>
/// check-localization.ps1 covers this repo-wide but runs against a baseline that suppresses 178
/// known findings — so a NEW key landing in eight files out of nine is exactly what a baselined
/// sweep can be talked into ignoring. Same shape as PhonePresenceTextTests.
/// </remarks>
public class ShellStatusKeysTests
{
    private static readonly string[] Keys =
    [
        "Shell_StatusFlyoutAddress",
        "Shell_StatusFlyoutHostLink",
        "Shell_StatusFlyoutLatency",
        "Shell_StatusFlyoutRuntime",
        "Shell_StatusOpenDiagnostics",
        "A11y_ConnectionStatusButton",
    ];

    [Fact]
    public void EnglishDeclaresThemAll()
    {
        var english = LoadResx("Strings.resx");

        foreach (var key in Keys)
            english.ContainsKey(key).Should().BeTrue($"Strings.resx does not declare {key}");
    }

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.hi.resx")]
    [InlineData("Strings.id.resx")]
    [InlineData("Strings.pl.resx")]
    [InlineData("Strings.pt-BR.resx")]
    [InlineData("Strings.tr.resx")]
    [InlineData("Strings.uk.resx")]
    public void EveryLocaleCarriesThem(string fileName)
    {
        var localized = LoadResx(fileName);

        foreach (var key in Keys)
            localized.ContainsKey(key).Should().BeTrue($"{fileName} does not declare {key}");
    }

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.hi.resx")]
    [InlineData("Strings.id.resx")]
    [InlineData("Strings.pl.resx")]
    [InlineData("Strings.pt-BR.resx")]
    [InlineData("Strings.tr.resx")]
    [InlineData("Strings.uk.resx")]
    public void NoneOfThemIsLeftAsTheEnglishPlaceholder(string fileName)
    {
        // A COPIED ENGLISH VALUE PASSES A CONTAINS-KEY CHECK AND SHIPS UNTRANSLATED. These six are
        // short labels, so the temptation to paste is real. Proper nouns would be a legitimate
        // exception; none of these six is one.
        var english = LoadResx("Strings.resx");
        var localized = LoadResx(fileName);

        foreach (var key in Keys)
            localized[key].Should().NotBe(english[key], $"{fileName}:{key} is still the English text");
    }

    private static System.Collections.Generic.Dictionary<string, string> LoadResx(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", fileName);
        File.Exists(path).Should().BeTrue($"Not found: {path}");

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusKeysTests"
```

Expected: FAIL — `Strings.resx does not declare Shell_StatusFlyoutAddress`.

- [ ] **Step 3: Add the English strings**

In `remex.desktop/Localization/Strings.resx`, beside the existing `Shell_PhonePresenceHostDown`
entry (around `:2982`), matching the surrounding `<data>` formatting exactly:

```xml
  <data name="Shell_StatusFlyoutAddress" xml:space="preserve">
    <value>Address</value>
  </data>
  <data name="Shell_StatusFlyoutHostLink" xml:space="preserve">
    <value>Host link</value>
  </data>
  <data name="Shell_StatusFlyoutLatency" xml:space="preserve">
    <value>Latency</value>
  </data>
  <data name="Shell_StatusFlyoutRuntime" xml:space="preserve">
    <value>Host runtime</value>
  </data>
  <data name="Shell_StatusOpenDiagnostics" xml:space="preserve">
    <value>Diagnostics</value>
  </data>
  <data name="A11y_ConnectionStatusButton" xml:space="preserve">
    <value>Connection status</value>
  </data>
```

- [ ] **Step 4: Add the eight translations**

Same six keys in each locale file, in the same position relative to `Shell_PhonePresenceHostDown`:

| Key | es | fr | hi | id |
|---|---|---|---|---|
| `Shell_StatusFlyoutAddress` | Dirección | Adresse | पता | Alamat |
| `Shell_StatusFlyoutHostLink` | Enlace con el host | Liaison avec l'hôte | होस्ट लिंक | Tautan host |
| `Shell_StatusFlyoutLatency` | Latencia | Latence | विलंबता | Latensi |
| `Shell_StatusFlyoutRuntime` | Entorno del host | Environnement de l'hôte | होस्ट रनटाइम | Runtime host |
| `Shell_StatusOpenDiagnostics` | Diagnóstico | Diagnostics | निदान | Diagnostik |
| `A11y_ConnectionStatusButton` | Estado de la conexión | État de la connexion | कनेक्शन स्थिति | Status koneksi |

| Key | pl | pt-BR | tr | uk |
|---|---|---|---|---|
| `Shell_StatusFlyoutAddress` | Adres | Endereço | Adres | Адреса |
| `Shell_StatusFlyoutHostLink` | Połączenie z hostem | Conexão com o host | Ana makine bağlantısı | Зв'язок із хостом |
| `Shell_StatusFlyoutLatency` | Opóźnienie | Latência | Gecikme | Затримка |
| `Shell_StatusFlyoutRuntime` | Środowisko hosta | Ambiente do host | Ana makine ortamı | Середовище хоста |
| `Shell_StatusOpenDiagnostics` | Diagnostyka | Diagnóstico | Tanılama | Діагностика |
| `A11y_ConnectionStatusButton` | Stan połączenia | Status da conexão | Bağlantı durumu | Стан з'єднання |

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusKeysTests"
```

Expected: PASS, 17 tests.

- [ ] **Step 6: Run the repo-wide localization check**

```
pwsh -File scripts/check-localization.ps1
```

Expected: no NEW findings beyond the existing baseline. If it reports new ones for these six
keys, fix them here rather than adding to the baseline.

- [ ] **Step 7: Commit**

```bash
git status --short
git add remex.desktop/Localization remex.desktop.tests/Services/ShellStatusKeysTests.cs
git commit -m "feat(desktop): shell status control strings in all nine locales (RemEx-44gc6)"
```

---

### Task 5: The control — button, icon, tooltip

**Files:**
- Modify: `remex.desktop/App.axaml:43` (icon block), `remex.desktop/Views/ShellView.axaml:237-254`, `remex.desktop/ViewModels/ShellViewModel.cs`
- Test: `remex.desktop.tests/Views/ShellStatusControlTests.cs` (create)

**Interfaces:**
- Consumes: `PhonePresenceMonitor.State`/`IsHostDown`/`HasNoPhone` (Task 2), `ShellStatusText.Tooltip` (Task 3), the six resource keys (Task 4).
- Produces: `ShellViewModel.StatusTooltip` (`string`) — Task 6 does not need it, but the flyout shares the control.

**Re-read before editing:** the three `StatusDotPresenceBindingTests` constraints at the top
of this plan. The dot stays, bound to `Presence.IsPhoneAttached`.

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/Views/ShellStatusControlTests.cs`:

```csharp
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The nav-drawer status control carries information while the drawer is COLLAPSED
/// (RemEx-44gc6).
/// </summary>
/// <remarks>
/// A SOURCE-TEXT TEST, the same shape as StatusDotPresenceBindingTests, and for the same reason:
/// Avalonia binding failures are silent and there is no headless render here that would notice a
/// missing tooltip. The reported defect was a control that in its collapsed state was a rounded
/// border containing one dot - indistinguishable from a ToggleSwitch, and carrying one bit.
/// </remarks>
public class ShellStatusControlTests
{
    private static string ShellView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    [Fact]
    public void TheStatusControlHasATooltip()
    {
        // THE WHOLE POINT OF THE BEAD. Collapsed, every text line in this control is hidden by
        // IsDrawerOpen, so without this the control says nothing but "green or red".
        ShellView().Should().Contain("ToolTip.Tip=\"{Binding StatusTooltip}\"",
            "the collapsed drawer has no other information channel");
    }

    [Fact]
    public void TheStatusControlIsAButtonRatherThanABareBorder()
    {
        ShellView().Should().Contain("Classes=\"status-card\"",
            "a Border is not focusable and cannot open a flyout - the control has to be a Button");
    }

    [Fact]
    public void TheDotIsNoLongerTheOnlyThingInTheCollapsedState()
    {
        ShellView().Should().Contain("IconPhone",
            "a dot alone in a rounded border is the ToggleSwitch silhouette this bead is about");
    }

    [Fact]
    public void TheAccessibleNameIsOnTheButtonAndNotAlsoOnTheDot()
    {
        // RemEx-x12a, restated at PhonePresenceMonitor.cs:62-68: naming both announces the same
        // text twice. The name belongs on the thing you can focus.
        var text = ShellView();

        text.Should().Contain("AutomationProperties.Name=\"{Binding Presence.PresenceAccessibleName}\"");
        text.Should().NotContain(
            "<Ellipse AutomationProperties.Name=\"{Binding Presence.PresenceAccessibleName}\" Grid.Column=\"0\"",
            "the dot's own accessible name must come off when the button gains one");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusControlTests"
```

Expected: FAIL — all four, starting with the tooltip assertion.

- [ ] **Step 3: Add the two icons**

In `remex.desktop/App.axaml`, after the `IconFiles` entry at `:43`:

```xml
            <!-- Shell connection status (RemEx-44gc6) -->
            <StreamGeometry x:Key="IconPhone">M17,19H7V5H17M17,1H7C5.89,1 5,1.89 5,3V21A2,2 0 0,0 7,23H17A2,2 0 0,0 19,21V3C19,1.89 18.1,1 17,1Z</StreamGeometry>
            <StreamGeometry x:Key="IconChevron">M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z</StreamGeometry>
```

- [ ] **Step 4: Add `StatusTooltip` to `ShellViewModel`**

In `remex.desktop/ViewModels/ShellViewModel.cs`, beside the `Presence` property (`:342`):

```csharp
    /// <summary>
    /// The one line the COLLAPSED nav drawer can show (RemEx-44gc6).
    /// </summary>
    /// <remarks>
    /// COMPOSED HERE BECAUSE THIS IS THE ONLY OBJECT THAT HOLDS BOTH HALVES. Presence is a
    /// process-wide singleton that deliberately knows nothing about the host link (RemEx-porg);
    /// Connection knows nothing about phones. The join is the shell's, and the composition itself
    /// is pure and tested in <see cref="Remex.Desktop.Services.ShellStatusText"/>.
    /// </remarks>
    public string StatusTooltip => Services.ShellStatusText.Tooltip(
        Presence.PresenceText,
        Connection.StatusText,
        Connection.IsConnected ? Connection.LatencyText : null);
```

Then, in the `ShellViewModel` constructor, re-raise it when either source changes:

```csharp
        // BOTH SOURCES, because the tooltip is a join of the two and a change in either makes it
        // stale. Presence ticks every 3 seconds; Connection changes on connect/disconnect.
        Presence.PropertyChanged += OnStatusSourceChanged;
        Connection.PropertyChanged += OnStatusSourceChanged;
```

Add the handler as a private method on `ShellViewModel`:

```csharp
    private void OnStatusSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(StatusTooltip));
```

- [ ] **Step 4b: Unsubscribe in `Dispose`**

`PhonePresenceMonitor.Instance` is a process-wide singleton that is **deliberately never
disposed** (`PhonePresenceMonitor.cs:36-44`), so a handler attached to it lives as long as the
process. `ShellViewModel` already implements `IDisposable` (`:344`). Add to its `Dispose`:

```csharp
        // THE SINGLETON OUTLIVES THIS VIEW MODEL. PhonePresenceMonitor is process-wide and has no
        // tear-down by design, so a subscription left behind roots a dead ShellViewModel for the
        // life of the process — and every surviving handler re-raises on every 3-second tick.
        Presence.PropertyChanged -= OnStatusSourceChanged;
        Connection.PropertyChanged -= OnStatusSourceChanged;
```

A lambda cannot be unsubscribed, which is why Step 4 uses a named method.

- [ ] **Step 5: Rebuild the control**

In `remex.desktop/Views/ShellView.axaml`, replace lines 237-254 (the `<!-- ═══ Bottom: Connection Status ═══ -->`
comment through the closing `</Border>`) with:

```xml
                                <!-- ═══ Bottom: Connection Status ═══ -->
                                <!-- A BUTTON, NOT A BORDER (RemEx-44gc6). Collapsed, the text column below
                                     is hidden by IsDrawerOpen — so what was left was a rounded border
                                     containing one dot, which is the ToggleSwitch silhouette the bead was
                                     filed about and carries exactly one bit. The tooltip is the fix: it is
                                     the only information channel the collapsed drawer has. The Button also
                                     makes the control focusable, which a Border never was. -->
                                <Button Grid.Row="2" Classes="status-card" Margin="12,4,12,16" Padding="4"
                                        HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"
                                        Background="{DynamicResource CardBackgroundBrush}"
                                        BorderBrush="{DynamicResource CardBorderBrush}" BorderThickness="1"
                                        CornerRadius="{DynamicResource CornerRadiusMedium}" Cursor="Hand"
                                        ToolTip.Tip="{Binding StatusTooltip}"
                                        AutomationProperties.Name="{Binding Presence.PresenceAccessibleName}">
                                    <Grid ColumnDefinitions="64,*,Auto">
                                        <Panel Grid.Column="0" HorizontalAlignment="Center" VerticalAlignment="Center">
                                            <Path Data="{StaticResource IconPhone}" Width="20" Height="20"
                                                  Stretch="Uniform" Fill="{DynamicResource TextSecondaryBrush}"/>
                                            <!-- THE DOT STAYS, AND IT STAYS BOUND TO PRESENCE.
                                                 StatusDotPresenceBindingTests requires ShellView to carry a
                                                 status dot bound to Presence.IsPhoneAttached; deleting it
                                                 would "fix" that test by removing the indicator. It is a
                                                 badge now rather than the whole control. Its own
                                                 AutomationProperties.Name is deliberately gone — the Button
                                                 above carries it, and naming both announces it twice
                                                 (RemEx-x12a). -->
                                            <Ellipse Width="8" Height="8" Classes="status-dot"
                                                     Classes.connected="{Binding Presence.IsPhoneAttached}"
                                                     HorizontalAlignment="Right" VerticalAlignment="Bottom"
                                                     Margin="0,0,-2,-2"/>
                                        </Panel>
                                        <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="8,0,4,0" IsVisible="{Binding IsDrawerOpen}">
                                            <!-- PHONE PRESENCE IS THE HEADLINE, the loopback link is the detail
                                                 below it (RemEx-0z7w / RemEx-porg). Connection.IsConnected is the
                                                 UI's own socket to its embedded host and is up essentially always,
                                                 so leading with it told a user with no phone paired that they were
                                                 "Connected" — the one fact this panel exists to convey, absent.
                                                 The dot beside it is bound to phone presence for the same reason. -->
                                            <TextBlock Text="{Binding Presence.PresenceText}" FontSize="11" FontWeight="Bold" Foreground="{DynamicResource TextPrimaryBrush}" TextTrimming="CharacterEllipsis"/>
                                            <TextBlock Text="{Binding Connection.StatusText}" FontSize="10" Foreground="{DynamicResource TextMutedBrush}" TextTrimming="CharacterEllipsis"/>
                                            <TextBlock Text="{Binding Connection.LatencyText}" FontSize="10" Foreground="{DynamicResource TextMutedBrush}" IsVisible="{Binding Connection.IsConnected}"/>
                                        </StackPanel>
                                        <Path Grid.Column="2" Data="{StaticResource IconChevron}" Width="10" Height="10"
                                              Stretch="Uniform" Fill="{DynamicResource TextMutedBrush}"
                                              VerticalAlignment="Center" Margin="0,0,8,0"
                                              IsVisible="{Binding IsDrawerOpen}"/>
                                    </Grid>
                                </Button>
```

- [ ] **Step 6: Add the `status-card` style**

In `remex.desktop/App.axaml`, beside the other `Button` styles:

```xml
        <!-- The nav drawer's connection status control (RemEx-44gc6). A Button so it is focusable
             and can host a flyout; styled flat so it still reads as a status panel. -->
        <Style Selector="Button.status-card">
            <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
        </Style>
        <Style Selector="Button.status-card:pointerover /template/ ContentPresenter">
            <Setter Property="Background" Value="{DynamicResource CardBackgroundHoverBrush}"/>
        </Style>
        <Style Selector="Button.status-card:focus-visible">
            <Setter Property="BorderBrush" Value="{DynamicResource AccentPrimaryBrush}"/>
            <Setter Property="BorderThickness" Value="2"/>
        </Style>
```

- [ ] **Step 7: Run the new tests and the guard tests together**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusControlTests|FullyQualifiedName~StatusDotPresenceBindingTests"
```

Expected: PASS, 4 new + 4 existing. **If `TheDotsThatWereFixedActuallyBindPresence` fails, the
dot lost its `Presence.IsPhoneAttached` binding — put it back rather than editing the test.**

- [ ] **Step 8: Build the desktop project and confirm the XAML compiles**

```
dotnet build remex.agent -c Release
```

Expected: exit code 0. Avalonia XAML errors surface here, not in the test run.
**Check the exit code before believing anything below it.** One project per invocation.

- [ ] **Step 9: Commit**

```bash
git status --short
git add remex.desktop/App.axaml remex.desktop/Views/ShellView.axaml remex.desktop/ViewModels/ShellViewModel.cs remex.desktop.tests/Views/ShellStatusControlTests.cs
git commit -m "feat(desktop): nav-drawer status becomes a button with an icon and a tooltip (RemEx-44gc6)"
```

---

### Task 6: The details flyout

**Files:**
- Modify: `remex.desktop/Views/ShellView.axaml` (the `Button` from Task 5)
- Test: `remex.desktop.tests/Views/ShellStatusFlyoutTests.cs` (create)

**Interfaces:**
- Consumes: everything from Tasks 2–5.
- Produces: nothing further.

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/Views/ShellStatusFlyoutTests.cs`:

```csharp
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The status flyout's contents and, more importantly, what it must NOT offer (RemEx-44gc6).
/// </summary>
public class ShellStatusFlyoutTests
{
    private static string ShellView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    [Fact]
    public void EachStateOffersItsOwnAction()
    {
        var text = ShellView();

        text.Should().Contain("Presence.IsHostDown", "host-down needs Diagnostics and Reconnect");
        text.Should().Contain("Presence.HasNoPhone", "no-phone needs a route to pairing");
    }

    [Fact]
    public void TheFlyoutShowsTheDetailTheCollapsedRowCannot()
    {
        var text = ShellView();

        text.Should().Contain("Presence.RemoteAddress");
        text.Should().Contain("Connection.HostRuntimeSummary");
    }

    [Fact]
    public void TheFlyoutDoesNotOfferDisconnect()
    {
        // DELIBERATE, AND WORTH PINNING. Disconnect is destructive-adjacent, it already exists on
        // SettingsView and HomeView, and a flyout dismissed by clicking anywhere outside it is the
        // wrong home for an action that costs a reconnect round-trip to undo. If this ever fails,
        // somebody added it without reading docs/SPEC-shell-connection-status.md section 4.
        var text = ShellView();
        var flyoutStart = text.IndexOf("<Button.Flyout>", System.StringComparison.Ordinal);
        var flyoutEnd = text.IndexOf("</Button.Flyout>", System.StringComparison.Ordinal);

        flyoutStart.Should().BeGreaterThan(-1, "the status button should have a flyout");
        flyoutEnd.Should().BeGreaterThan(flyoutStart);

        text[flyoutStart..flyoutEnd].Should().NotContain("DisconnectCommand");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatusFlyoutTests"
```

Expected: FAIL — `the status button should have a flyout`.

- [ ] **Step 3: Add the flyout**

Immediately after the `<Button ... >` opening tag from Task 5 and before its `<Grid ...>`:

```xml
                                    <Button.Flyout>
                                        <Flyout Placement="Right">
                                            <Border Classes="glass-card" Padding="16" MaxWidth="320">
                                                <StackPanel Spacing="10">
                                                    <!-- Presence dot bound to presence, NEVER to
                                                         Connection.IsConnected — StatusDotPresenceBindingTests
                                                         scans every axaml under remex.desktop for exactly that. -->
                                                    <StackPanel Orientation="Horizontal" Spacing="10">
                                                        <Ellipse Width="10" Height="10" VerticalAlignment="Center"
                                                                 Classes="status-dot"
                                                                 Classes.connected="{Binding Presence.IsPhoneAttached}"/>
                                                        <TextBlock Text="{Binding Presence.PresenceText}" FontSize="14" FontWeight="Bold"
                                                                   Foreground="{DynamicResource TextPrimaryBrush}" TextWrapping="Wrap"/>
                                                    </StackPanel>

                                                    <Separator Height="1" Background="{DynamicResource CardBorderBrush}" Opacity="0.3"/>

                                                    <!-- Every row hides when its binding is empty, so the flyout
                                                         shrinks rather than showing labelled blanks. -->
                                                    <Grid ColumnDefinitions="Auto,*" IsVisible="{Binding Presence.RemoteAddress, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
                                                        <TextBlock Grid.Column="0" Text="{conv:Localize Shell_StatusFlyoutAddress}" FontSize="12" Foreground="{DynamicResource TextMutedBrush}" Margin="0,0,12,0"/>
                                                        <TextBlock Grid.Column="1" Text="{Binding Presence.RemoteAddress}" FontSize="12" FontFamily="Consolas,monospace" Foreground="{DynamicResource TextSecondaryBrush}" HorizontalAlignment="Right"/>
                                                    </Grid>
                                                    <Grid ColumnDefinitions="Auto,*">
                                                        <TextBlock Grid.Column="0" Text="{conv:Localize Shell_StatusFlyoutHostLink}" FontSize="12" Foreground="{DynamicResource TextMutedBrush}" Margin="0,0,12,0"/>
                                                        <TextBlock Grid.Column="1" Text="{Binding Connection.StatusText}" FontSize="12" Foreground="{DynamicResource TextSecondaryBrush}" HorizontalAlignment="Right" TextWrapping="Wrap"/>
                                                    </Grid>
                                                    <Grid ColumnDefinitions="Auto,*" IsVisible="{Binding Connection.IsConnected}">
                                                        <TextBlock Grid.Column="0" Text="{conv:Localize Shell_StatusFlyoutLatency}" FontSize="12" Foreground="{DynamicResource TextMutedBrush}" Margin="0,0,12,0"/>
                                                        <TextBlock Grid.Column="1" Text="{Binding Connection.LatencyText}" FontSize="12" Foreground="{DynamicResource AccentPrimaryBrush}" HorizontalAlignment="Right"/>
                                                    </Grid>
                                                    <Grid ColumnDefinitions="Auto,*" IsVisible="{Binding Connection.IsConnected}">
                                                        <TextBlock Grid.Column="0" Text="{conv:Localize Shell_StatusFlyoutRuntime}" FontSize="12" Foreground="{DynamicResource TextMutedBrush}" Margin="0,0,12,0"/>
                                                        <TextBlock Grid.Column="1" Text="{Binding Connection.HostRuntimeSummary}" FontSize="12" Foreground="{DynamicResource TextSecondaryBrush}" HorizontalAlignment="Right" TextWrapping="Wrap"/>
                                                    </Grid>

                                                    <Separator Height="1" Background="{DynamicResource CardBorderBrush}" Opacity="0.3"/>

                                                    <!-- ONE OBVIOUS NEXT STEP PER STATE, except host-down, which is
                                                         the only one where the user needs to both understand and
                                                         act. NO DISCONNECT here — see the spec, section 4. -->
                                                    <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding Presence.IsHostDown}">
                                                        <Button Content="{conv:Localize Shell_StatusOpenDiagnostics}" Command="{Binding NavigateToDiagnosticLogsCommand}" Background="{DynamicResource CardBackgroundBrush}" Padding="12,6" CornerRadius="6" FontSize="12" FontWeight="SemiBold" Cursor="Hand"/>
                                                        <Button Content="{conv:Localize Btn_Connect}" Command="{Binding Connection.ConnectCommand}" Background="{DynamicResource AccentPrimaryBrush}" Foreground="{DynamicResource AccentForegroundBrush}" Padding="12,6" CornerRadius="6" FontSize="12" FontWeight="SemiBold" Cursor="Hand"/>
                                                    </StackPanel>
                                                    <Button Content="{conv:Localize Home_PairPhoneButton}" Command="{Binding NavigateToSettingsCommand}" IsVisible="{Binding Presence.HasNoPhone}" HorizontalAlignment="Left" Background="{DynamicResource AccentPrimaryBrush}" Foreground="{DynamicResource AccentForegroundBrush}" Padding="12,6" CornerRadius="6" FontSize="12" FontWeight="SemiBold" Cursor="Hand"/>
                                                    <Button Content="{conv:Localize Nav_Settings}" Command="{Binding NavigateToSettingsCommand}" IsVisible="{Binding Presence.IsPhoneAttached}" HorizontalAlignment="Left" Background="{DynamicResource CardBackgroundBrush}" Padding="12,6" CornerRadius="6" FontSize="12" FontWeight="SemiBold" Cursor="Hand"/>
                                                </StackPanel>
                                            </Border>
                                        </Flyout>
                                    </Button.Flyout>
```

**Note the markup extension prefix:** `ShellView.axaml` uses `conv:Localize`, not `local:Localize`
(see `ShellView.axaml:219`). Copy the prefix from the file you are editing, not from this plan.

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~ShellStatus|FullyQualifiedName~StatusDotPresenceBindingTests"
```

Expected: PASS — 3 flyout + 4 control + 4 guard tests.

- [ ] **Step 5: Build and confirm the XAML compiles**

```
dotnet build remex.agent -c Release
```

Expected: exit code 0.

- [ ] **Step 6: Commit**

```bash
git status --short
git add remex.desktop/Views/ShellView.axaml remex.desktop.tests/Views/ShellStatusFlyoutTests.cs
git commit -m "feat(desktop): connection status flyout with per-state actions (RemEx-44gc6)"
```

---

### Task 7: Verify the control in all four PC themes

**Files:** none — this is a manual verification gate.

`AGENTS.md:139-141`: the four themes have distinct contrast ratios and background treatments,
and a flyout floating over page content is exactly where a background treatment fails. The
`glass-card` class is used by every settings card, so it is the safest starting point — but it
has never been used inside a `Flyout` before.

- [ ] **Step 1: Deploy the build**

```
pwsh -File scripts/update-local-install.ps1
```

- [ ] **Step 2: Launch the installed exe**

```
& "C:\Program Files\RemEx\Remex.Agent.exe"
```

Not `dotnet <dll>` — that trips a .NET Host firewall prompt and refuses inbound connections.

- [ ] **Step 3: For each of CyberNOC, Monolith, SolarFlare, BaseDarkGlass, check**

- [ ] Collapsed drawer: hovering the control shows a tooltip with presence, host link and latency
- [ ] Collapsed drawer: the phone icon and its badge dot are both legible against the card background
- [ ] Expanded drawer: the chevron is visible and the three text lines are unchanged
- [ ] Clicking opens the flyout; its border is distinguishable from the page behind it
- [ ] Flyout text is legible — **SolarFlare is the usual failure**, per `RemEx-lki2r`
- [ ] Tab to the control: the focus ring is visible

- [ ] **Step 4: Record the result**

```bash
bd update RemEx-44gc6 --append-notes "Four-theme verification on the installed exe: <result per theme>."
```

---

### Task 8: The trust card shows friendly names

**Files:**
- Modify: `remex.desktop/ViewModels/SettingsViewModel.cs` (`FileTrustDeviceItem` at `:1402-1444`, `ReplaceTrustedDevices` at `:923-938`, `RevokeTrustAsync` at `:1020`, `ApplyPairedDeviceRename` at `:416`)
- Modify: `remex.desktop/Views/SettingsView.axaml:398`
- Modify: `remex.desktop.tests/ViewModels/DestructiveActionFailClosedTests.cs:586`
- Test: `remex.desktop.tests/ViewModels/FileTrustDisplayNameTests.cs` (create)

**Interfaces:**
- Consumes: `PairedDeviceDisplayName.Resolve(string deviceId, IReadOnlyDictionary<string,string>? names)` — existing, unchanged.
- Produces: `FileTrustDeviceItem.DisplayName` (`string`, never blank).

**Bead:** `RemEx-9me77`. Independent of Tasks 1–7 — it can be done first if preferred.

- [ ] **Step 1: Write the failing test**

Create `remex.desktop.tests/ViewModels/FileTrustDisplayNameTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The File-Sharing Trust card shows the name the user chose, like every other card
/// (RemEx-9me77).
/// </summary>
/// <remarks>
/// PairedDeviceDisplayName's own XML doc cites THIS list as the bad example it was written to
/// avoid — "the existing File-Sharing Trust list already renders raw ShortIds and is described
/// as opaque". The helper shipped; it was never wired in here. A row reading "07ca4e9d5383…"
/// with a Revoke button beside it is a decision the user cannot make safely.
/// </remarks>
public class FileTrustDisplayNameTests
{
    private const string DeviceId = "07ca4e9d5383a1b2c3d4";

    [Fact]
    public void TheUsersNameWins()
    {
        var names = new Dictionary<string, string> { [DeviceId] = "Connor's Galaxy" };

        var item = new FileTrustDeviceItem(DeviceId, true, false, names);

        item.DisplayName.Should().Be("Connor's Galaxy");
    }

    [Fact]
    public void WithoutANameTheIdIsShownRatherThanNothing()
    {
        // Resolve's documented contract: NEVER blank. A trust entry can outlive its pairing, so
        // "no matching paired device" is a real state, and blank would be strictly worse than
        // opaque - at least an id is comparable against what the phone shows.
        var item = new FileTrustDeviceItem(DeviceId, true, false, null);

        item.DisplayName.Should().Be(DeviceId);
    }

    [Fact]
    public void TheShortIdSurvivesAsTheSecondaryLine()
    {
        // Kept, not replaced. The name answers "which device"; the id answers "is this the same
        // one my phone is showing me".
        var names = new Dictionary<string, string> { [DeviceId] = "Connor's Galaxy" };

        var item = new FileTrustDeviceItem(DeviceId, true, false, names);

        item.ShortId.Should().Be("07ca4e9d5383…");
    }

    [Fact]
    public void TheTrustToggleStateIsStillSeededWithoutWritingBack()
    {
        // THE EXISTING CONTRACT, and the new constructor parameter must not disturb it. The
        // _seeding flag suppresses the change events so LOADING the list does not persist a write
        // for every row.
        var raised = false;
        var item = new FileTrustDeviceItem(DeviceId, true, true, null);
        item.FullBrowseChanged += (_, _) => raised = true;

        item.FullBrowseGranted.Should().BeTrue();
        item.AutoAcceptIncoming.Should().BeTrue();
        raised.Should().BeFalse("construction seeds state and must not write back");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~FileTrustDisplayNameTests"
```

Expected: FAIL — `FileTrustDeviceItem` does not take 4 arguments.

- [ ] **Step 3: Add `DisplayName` to `FileTrustDeviceItem`**

In `remex.desktop/ViewModels/SettingsViewModel.cs`, replace `:1404-1407`:

```csharp
    public string ClientId { get; }

    /// <summary>
    /// What to call this device — the user's chosen name, falling back to the id.
    /// </summary>
    /// <remarks>
    /// THE SAME HELPER THE PAIRED-DEVICES CARD USES (RemEx-9me77). Those two cards sit on the same
    /// Settings page, and this one showed a raw truncated id while the one above it showed the name
    /// the user typed. PairedDeviceDisplayName.Resolve NEVER returns blank, so a trust entry that
    /// outlives its pairing still renders something comparable against what the phone shows.
    /// </remarks>
    public string DisplayName { get; }

    /// <summary>
    /// The leading characters of the client id, kept as a secondary line beneath the name.
    /// </summary>
    /// <remarks>
    /// NOT REPLACED BY <see cref="DisplayName"/>, demoted beneath it. The name answers "which
    /// device"; the id answers "is this the same one my phone is showing me", and a Revoke button
    /// sits beside both.
    /// </remarks>
    public string ShortId => ClientId.Length > 12 ? ClientId[..12] + "…" : ClientId;
```

And replace the constructor at `:1421-1428`:

```csharp
    public FileTrustDeviceItem(
        string clientId,
        bool fullBrowseGranted,
        bool autoAcceptIncoming,
        IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        ClientId = clientId;
        DisplayName = Services.PairedDeviceDisplayName.Resolve(clientId, deviceNames);
        _seeding = true;
        FullBrowseGranted = fullBrowseGranted;
        AutoAcceptIncoming = autoAcceptIncoming;
        _seeding = false;
    }
```

The default on `deviceNames` keeps any existing 3-argument call site compiling.

- [ ] **Step 4: Run the test to verify it passes**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~FileTrustDisplayNameTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Feed the name map in, from one place**

In `SettingsViewModel`, extract the map-building currently inline in `RefreshPairedDevices`
(`:281-286`) into a method, placed just above `RefreshPairedDevices`:

```csharp
    /// <summary>
    /// The friendly-name map, keyed by device id.
    /// </summary>
    /// <remarks>
    /// ONE BUILDER, TWO CARDS (RemEx-9me77). The paired-devices list and the file-sharing trust
    /// list both need it, and the second one going without is the whole bug — the two cards sit on
    /// the same page and disagreed about what a device is called. The user's override outranks the
    /// device's reported name, which is what PairedDeviceDisplayName.Resolve implements.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> BuildDeviceNames()
    {
        var source = ResolvePairedDeviceSource();
        if (source is null) return new Dictionary<string, string>(StringComparer.Ordinal);

        return source.PairedDevices()
            .Where(r => !string.IsNullOrWhiteSpace(r.NameOverride) || !string.IsNullOrWhiteSpace(r.DeviceName))
            .ToDictionary(
                r => r.ClientId,
                r => (r.NameOverride ?? r.DeviceName)!,
                StringComparer.Ordinal);
    }
```

In `RefreshPairedDevices`, replace the inline `var names = rows...ToDictionary(...)` block at
`:281-286` with `var names = BuildDeviceNames();` — keeping the explanatory comment at `:277-280`
attached to `BuildDeviceNames` rather than deleting it.

In `ReplaceTrustedDevices` (`:923`), build the map once and pass it:

```csharp
    private void ReplaceTrustedDevices(IReadOnlyList<FileTrustRecord> records)
    {
        foreach (var existing in TrustedDevices)
            UnsubscribeTrustDevice(existing);

        TrustedDevices.Clear();

        // ONCE, not per row: this walks the paired-device source, and a trust list is short but
        // not guaranteed to be.
        var names = BuildDeviceNames();

        foreach (var record in records)
        {
            var item = new FileTrustDeviceItem(
                record.ClientId, record.FullBrowseGranted, record.AutoAcceptIncoming, names);
            SubscribeTrustDevice(item);
            TrustedDevices.Add(item);
        }

        OnPropertyChanged(nameof(HasTrustedDevices));
    }
```

- [ ] **Step 6: Bind it in the view**

In `remex.desktop/Views/SettingsView.axaml`, replace line 398 with:

```xml
                                                <StackPanel Grid.Column="0" Spacing="2" VerticalAlignment="Center">
                                                    <TextBlock Text="{Binding DisplayName}" FontSize="14" FontWeight="Bold" Foreground="{DynamicResource TextPrimaryBrush}" TextTrimming="CharacterEllipsis"/>
                                                    <TextBlock Text="{Binding ShortId}" FontSize="11" FontFamily="Consolas,monospace" Foreground="{DynamicResource TextMutedBrush}"/>
                                                </StackPanel>
```

- [ ] **Step 7: Switch the revoke confirmation to the name**

In `SettingsViewModel.cs:1020`, change `item.ShortId` to `item.DisplayName`.

Then in `remex.desktop.tests/ViewModels/DestructiveActionFailClosedTests.cs:586`, change
`device.ShortId` to `device.DisplayName`. Update the assertion's reason string to keep naming
the property it pins:

```csharp
        body.Should().Contain(device.DisplayName,
            "the user must be told WHICH device they are about to cut off, not just that they are");
```

- [ ] **Step 8: Refresh the trust list after a rename**

In `ApplyPairedDeviceRename` (`:416`), after the existing refresh of the paired list, add:

```csharp
        // BOTH CARDS, OR THEY DISAGREE ON THE SAME PAGE (RemEx-9me77). The trust list resolves its
        // names from the same map this rename just changed, so refreshing one and not the other
        // leaves two visible rows for one device under two different names until the user happens
        // to press Refresh.
        _ = LoadTrustedDevicesAsync();
```

- [ ] **Step 9: Run the affected suites**

```
dotnet test remex.desktop.tests -c Release --filter "FullyQualifiedName~FileTrustDisplayNameTests|FullyQualifiedName~DestructiveActionFailClosed|FullyQualifiedName~PairedDeviceDisplayName"
```

Expected: PASS, with the total 4 higher than before.

- [ ] **Step 10: Build**

```
dotnet build remex.agent -c Release
```

Expected: exit code 0.

- [ ] **Step 11: Commit**

```bash
git status --short
git add remex.desktop/ViewModels/SettingsViewModel.cs remex.desktop/Views/SettingsView.axaml remex.desktop.tests/ViewModels/FileTrustDisplayNameTests.cs remex.desktop.tests/ViewModels/DestructiveActionFailClosedTests.cs
git commit -m "fix(desktop): file-sharing trust card shows the configurable device name (RemEx-9me77)"
```

---

### Task 9: Docs, full verification, and closing the beads

**Files:**
- Modify: `docs/CHANGELOG.md`, `docs/SPEC-shell-connection-status.md`

- [ ] **Step 1: Correct the spec's §2**

In `docs/SPEC-shell-connection-status.md`, the `SummaryTooltip` row of the §2 table describes a
property on `PhonePresenceMonitor`. Replace that row with:

```markdown
| — | — | `SummaryTooltip` moved: see `ShellStatusText.Tooltip` + `ShellViewModel.StatusTooltip` |
```

and add beneath the table:

> **Implementation note (Task 3).** The tooltip cannot live on `PhonePresenceMonitor`: two of its
> three parts come from `ConnectionViewModel`, and the monitor deliberately knows nothing about the
> host link (RemEx-porg). Composition is a pure helper, `Remex.Desktop.Services.ShellStatusText`;
> the join is exposed as `ShellViewModel.StatusTooltip`, which is the only object holding both halves.

Then correct the §6 key list, which shipped with two errors:

- **Drop `Shell_StatusTooltipFormat`.** `ShellStatusText.Tooltip` joins its parts with a newline
  and drops the blank ones, so there is no format string to localize. A format key would have
  forced a fixed number of parts, which is exactly what the disconnected case (no latency)
  cannot supply.
- **Add `Shell_StatusFlyoutRuntime`.** §4's detail table lists a Host runtime row but §6 never
  gave its label a key.

Net count is unchanged at six.

- [ ] **Step 2: Add the CHANGELOG entries**

At the top of the current unreleased section in `docs/CHANGELOG.md`, matching the indentation
and bullet style of the entries already there:

```markdown
- **Nav-drawer connection status is now a button with a tooltip and a details flyout**
  (`RemEx-44gc6`). Collapsed, the control was a rounded border containing a single dot — the
  silhouette of a toggle switch, carrying one bit of information, with no tooltip. It now shows
  a phone icon with a status badge, a tooltip that works in both drawer states, and a flyout with
  the peer address, host link, latency and host runtime, plus the one action that fits the current
  state. `PhonePresenceMonitor` gained `ShellConnectionState`, so "RemEx is not running on this PC"
  and "no phone attached" are no longer the same red dot. `IsPhoneAttached` is unchanged and the
  other four indicators still bind it.
- **The File-Sharing Trust card shows the device name you chose** (`RemEx-9me77`). It rendered a
  raw truncated client id while the Paired Devices card directly above it showed the configurable
  name. Both now resolve through `PairedDeviceDisplayName`; the id survives as a secondary line,
  and renaming a device refreshes both cards.
```

- [ ] **Step 3: Run the full gate**

```
pwsh -File scripts/verify.ps1 -Scope all
```

Expected: PASS. **Record the test total.**

New test cases by task: Task 1 = 3, Task 2 = 6, Task 3 = 5, Task 4 = 17, Task 5 = 4,
Task 6 = 3, Task 8 = 4. **Total 42.** So the suite total must be exactly 42 higher than the
baseline you recorded before Task 1.

Capture that baseline now if you have not already — a green run whose count did not rise did
not execute your new tests, and that is the single cheapest staleness detector available.
If the delta is not 42, find out why before closing anything; do not reconcile by adjusting
this number.

- [ ] **Step 4: Confirm every new file is tracked**

```bash
git status --short
git check-ignore -v docs/PLAN-shell-connection-status.md
```

`check-ignore` must print nothing for anything you intend to commit. `docs/superpowers/` is
gitignored at `.gitignore:119`; `docs/` is not.

- [ ] **Step 5: Commit the docs**

```bash
git status --short
git add docs/CHANGELOG.md docs/SPEC-shell-connection-status.md
git commit -m "docs: changelog and spec correction for the shell status rework (RemEx-44gc6)"
```

- [ ] **Step 6: Close the beads**

```bash
bd close RemEx-44gc6
bd close RemEx-9me77
```

Include in each closure: the `verify.ps1` result with its test TOTAL, the four-theme
verification result from Task 7, and — for `RemEx-44gc6` — the spec correction from Step 1.

---

## Notes for the implementer

**`RemEx-xt0af` is not in this plan.** The 🖴 volumes button on the File Transfer screen still
fails with "A paired client identity is required to browse volumes." That is a separate epic
with its own design pass. Do not fix it here, and do not hide the button here — deciding
whether to hide it in the interim is step one of that epic.

**The four themes are PC-only.** Nothing in this plan touches `remex.android`, so no M3
theming axis applies and no Android build is needed.
