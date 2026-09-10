---
name: ui-verify
description: Use when changing anything visual in remex.desktop - any .axaml, any ControlTheme or Style, any Material.Avalonia control, any of the remaining Material Design epic beads. Runs the Debug host with XAML hot reload, then screenshots the live window and dumps its UI Automation tree so the change is actually LOOKED AT. Invoke before claiming a UI change works, because remex.desktop.tests has no headless render and cannot see paint.
---

# Seeing the desktop UI

## Why this exists

`remex.desktop.tests` has **no headless render**. Every UI test in this repo is a text assertion over
`.axaml` source. That is a real limit, not an oversight, and it has already cost us twice in one day:

| Bug | What shipped | What the tests saw |
|---|---|---|
| RemEx-b8dxy | The entire shell covered by an opaque `SideSheet`. App rendered one flat #303030 rectangle with only the gear FAB on it. | 2939 green. One test actively *asserted the bug*. |
| RemEx-27a0s | Clicking outside the command palette raised a different application instead of RemEx. | 2939 green. Nothing models activation. |

Both were found by screenshotting the running window. Neither was findable any other way.

**So: a UI change is not verified until it has been looked at.** `verify.ps1` passing is necessary and
not sufficient for anything visual.

## The loop

```powershell
pwsh scripts/ui-hotreload.ps1 -Start      # builds Debug, launches it, hot reload ON
# ...edit any .axaml under remex.desktop. No rebuild, no restart.
pwsh scripts/ui-snapshot.ps1 -Screenshot -Tree
pwsh scripts/ui-hotreload.ps1 -Stop       # restores the installed Release build
```

Then **Read the .png**. That is the whole point; a snapshot nobody looks at verifies nothing.

Allow ~3-6s after saving a `.axaml` before snapshotting. `Alt+F5` in the app forces a full reload if
a change does not seem to have landed.

## Reading the two outputs together

`-Screenshot` is what the user sees. `-Tree` is what Avalonia thinks it laid out. The diagnosis is
almost always in the **comparison**, not in either alone:

- **Flat-colour screenshot + intact tree** → the shell is being *painted over*. Layout, focus and hit
  testing are all fine; something opaque is on top. This is RemEx-b8dxy exactly, and
  `ui-snapshot.ps1` warns when one colour covers ≥90% of the window. Go read the
  *Desktop shell - Material.Avalonia template parts* guard in `docs/REGRESSION-GUARDS.md`.
- **Missing/zero-size elements in the tree** → a real layout fault. The screenshot only confirms it.
- **Elements at negative coordinates** → normally correct: a closed drawer or side sheet parks
  offscreen. Check `offscreen=` and the window's own rect before calling it a bug. A window rect of
  `-32000,-32000` means minimised, and `ui-snapshot.ps1` restores it for you unless `-NoRestore`.

## Editing .axaml from a script

Preserve the UTF-8 **BOM**. Every `.axaml` here carries one, and PowerShell's `Set-Content` strips it,
which shows up as a spurious one-line diff on the file's first line:

```powershell
$enc = New-Object System.Text.UTF8Encoding($true)   # $true = emit BOM
$t = [System.IO.File]::ReadAllText($path)
[System.IO.File]::WriteAllText($path, $t.Replace($from, $to), $enc)
```

Prefer the `Edit` tool, which does not have this problem. This note is for bulk or scripted edits.

## What hot reload does and does not re-run

A reload rebuilds a control's visual tree **without re-running its constructor**. Anything a
code-behind does imperatively is therefore lost. `ShellView` is the live example: `PageHost.Content`
is deliberately unbound in XAML (`PageHostSequencer`, RemEx-lma2o), so before this was handled, every
reload of `ShellView.axaml` gave a shell with working chrome and a blank content area — which looks
exactly like RemEx-b8dxy and is not it.

The fix is a parameterless instance method named **`InitializeComponentState`**, which HotAvalonia
re-runs on reload. `ShellView.axaml.cs` has one; copy that pattern into any view whose code-behind
resolves controls by name, subscribes to specific control instances, or seeds content imperatively.

Discover it **by name, never with `[AvaloniaHotReload]`**. The `HotAvalonia` package is
`PrivateAssets="All"` and Debug-only, so an attribute reference is a Release compile error waiting to
happen.

## Constraints worth knowing before you reach for this

- **Hot reload is Debug-only, on purpose.** The shipped app is the Release publish in
  `C:\Program Files\RemEx` produced by `scripts/update-local-install.ps1`. Verified: no HotAvalonia,
  MonoMod or `Markup.Xaml.Loader` binary reaches either that publish or Program Files.
- **The installed-exe rule still applies to device work.** Memory and docs say host testing must run
  `C:\Program Files\RemEx\Remex.Agent.exe`, because running the DLL trips a .NET Host firewall prompt
  and refuses *inbound* connections. That is about the phone reaching the PC. Pure UI work needs no
  inbound anything, so the Debug host is fine for it — but anything touching pairing, streaming or a
  real device goes back to the installed build.
- **Always `-Stop` when you finish.** It kills the Debug host and relaunches the installed Release
  build. Leaving a Debug build running is how the next session ends up debugging the wrong binary.
- **Debug is not the shipping configuration.** For a change that could plausibly differ between
  configurations, take a final look at the installed Release build too.

## This does not replace a headless render harness

Hot reload speeds up *interactive* verification, by a human or by an agent. It does nothing for CI:
the test suite still cannot see a covered shell, and the next regression of the RemEx-b8dxy shape
will still go green. A headless render harness in `remex.desktop.tests` is a separate and still-open
piece of work. Do not treat this skill as having closed it.
