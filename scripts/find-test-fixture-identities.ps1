#Requires -Version 7.0
<#
.SYNOPSIS
    Reports test-fixture identities left behind in the machine-wide RemEx pairing and file-trust
    stores. Reports only — it never edits the live stores.

.DESCRIPTION
    Before RemEx-4u29 the test suite resolved the SAME machine-wide store the host uses, so fixtures
    wrote real entries into it: seven fixture identities were found in paired_clients.json on the
    developer's PC (attacker-phone, victim-phone, bodyless-phone, probe-phone, volumes-phone,
    reconnect-name-client, integration-test-client-1) beside four genuine client ids, and a fixture's
    fullBrowseGranted record in file_transfer_trust.json. The tests can no longer do this. What they
    already wrote is still there, and this script is how you find it.

    WHY THIS DOES NOT DELETE ANYTHING. The same two files hold the user's REAL pairings: an entry in
    paired_clients.json is a credential record, and deleting the wrong one unpairs a phone with no
    warning and no way back short of re-pairing by PIN. The fixture test below is a heuristic —
    genuine client ids are 32 hex characters, fixtures are readable names — and a heuristic is not
    something that should be allowed to revoke credentials unattended. So: this reports, and with
    -WriteProposal it writes a cleaned COPY for you to inspect and move into place yourself. The live
    files are never touched.

    WHERE THE PROPOSAL GOES, AND WHY NOT BESIDE THE STORE. A proposal for paired_clients.json still
    contains every genuine client's base64 reconnect secret. C:\ProgramData\RemEx inherits an ACL
    granting BUILTIN\Users write access; the live paired_clients.json escapes it because
    PairedClientRegistry disables inheritance on that one file (RestrictStorePermissions) precisely so
    the broad ProgramData ACL does not leak the secrets. A copy written into that directory would NOT
    escape it, and would be readable by any local account — review caught the first version of this
    script doing exactly that. Proposals therefore go to a per-user temp directory, and the file is
    hardened as it is written. If you do move one into place, re-apply the restriction afterwards or
    the store keeps the loose inherited ACL until the next pairing rewrites it.

.PARAMETER StoreDirectory
    Directory holding the stores. Defaults to the machine-wide location, C:\ProgramData\RemEx on
    Windows and $HOME/.local/share/Remex elsewhere.

.PARAMETER WriteProposal
    Also write <store>.cleaned.json holding the entries that would remain, into -ProposalDirectory.
    Nothing is moved into place; that is a decision for a person who can tell which phones are theirs.

.PARAMETER ProposalDirectory
    Where -WriteProposal writes. Defaults to a per-user temp directory rather than the store
    directory. On Windows %TEMP% is already owner-only; on Linux and macOS the temp root is mode 1777
    and is NOT, which is why the directory is hardened explicitly (see Protect-ProposalDirectory).
    Created if absent.

.EXAMPLE
    ./scripts/find-test-fixture-identities.ps1
    ./scripts/find-test-fixture-identities.ps1 -WriteProposal
#>
[CmdletBinding()]
param(
    [string] $StoreDirectory,
    [switch] $WriteProposal,
    [string] $ProposalDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $StoreDirectory) {
    $StoreDirectory = if ($IsWindows) {
        Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'RemEx'
    } else {
        Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Remex'
    }
}

if (-not $ProposalDirectory) {
    $ProposalDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'remex-store-proposals'
}

# A real client id is the 32-character hex the client generates. Everything else in these files was
# put there by a fixture, which is the whole tell — no genuine client has ever had a readable name.
function Test-IsGenuineClientId([string] $ClientId) {
    return $ClientId -match '^[0-9a-fA-F]{32}$'
}

# Owner-only on the CONTAINING DIRECTORY, applied before any proposal is written into it. On Windows
# the per-user %TEMP% is already owner-only so this is a no-op; on Linux and macOS the temp root is
# world-writable, and a proposal holding every genuine client's reconnect secret must not sit in a
# world-readable directory at a predictable path even for the instant before the file is hardened.
function Protect-ProposalDirectory([string] $Path) {
    if (-not $IsWindows) {
        # $LASTEXITCODE explicitly, because $ErrorActionPreference='Stop' does NOT stop on a failing
        # NATIVE command until PowerShell 7.4 ($PSNativeCommandUseErrorActionPreference), and this
        # script only requires 7.0. A chmod that failed - because another local user got to this
        # predictable path first - would otherwise be skipped over, and the next thing this script
        # does is write every genuine client's reconnect secret into that path.
        chmod 700 $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict permissions on '$Path' (chmod exited $LASTEXITCODE). Refusing to write a proposal there."
        }
    }
}

# Owner-only, matching what PairedClientRegistry.RestrictStorePermissions applies to the live store.
# A proposal for paired_clients.json holds every genuine client's reconnect secret, so it is key
# material and gets the same treatment as the file it was derived from.
function Protect-ProposalFile([string] $Path) {
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)   # break inheritance, copy nothing across
        foreach ($rule in @($acl.Access)) { [void] $acl.RemoveAccessRule($rule) }
        foreach ($identity in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', [Security.Principal.WindowsIdentity]::GetCurrent().Name)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                $identity, 'FullControl', 'None', 'None', 'Allow'))
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    } else {
        # Same reasoning as Protect-ProposalDirectory: a silently-failed chmod here leaves key
        # material world-readable.
        chmod 600 $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict permissions on '$Path' (chmod exited $LASTEXITCODE). Refusing to write key material there."
        }
    }
}

$stores = @(
    @{ Name = 'paired_clients.json';      What = 'pairing credential' },
    @{ Name = 'paired_client_names.json'; What = 'remembered device name' },
    @{ Name = 'file_transfer_trust.json'; What = 'file-browse authorisation' }
)

Write-Host "Scanning $StoreDirectory" -ForegroundColor Cyan
$totalSuspect = 0

foreach ($store in $stores) {
    $path = Join-Path $StoreDirectory $store.Name
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "  $($store.Name): not present" -ForegroundColor DarkGray
        continue
    }

    try {
        # -AsHashtable keeps the client ids as literal keys; all three stores are objects keyed by
        # client id, so ordinary property access would mangle ids that collide with PowerShell's own
        # member names.
        $entries = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        Write-Host "  $($store.Name): UNREADABLE ($($_.Exception.Message))" -ForegroundColor Yellow
        continue
    }

    # paired_clients.json also has a legacy array shape (["clientId", ...]) that PairedClientRegistry
    # still loads. -AsHashtable turns that into a list, whose .Keys is empty — so without this check a
    # legacy store consisting ENTIRELY of fixtures would be reported as clean. Review caught it.
    if ($null -ne $entries -and $entries -isnot [System.Collections.IDictionary]) {
        $legacyIds = @(@($entries) | Where-Object { $_ -is [string] })
        $legacySuspect = @($legacyIds | Where-Object { -not (Test-IsGenuineClientId $_) } | Sort-Object)
        $totalSuspect += $legacySuspect.Count
        Write-Host "  $($store.Name): LEGACY ARRAY FORMAT - $($legacySuspect.Count) of $($legacyIds.Count) entr(y/ies) look like fixtures" -ForegroundColor Yellow
        foreach ($id in $legacySuspect) { Write-Host "      $id" }
        Write-Host '      no proposal written for the legacy format - edit it by hand' -ForegroundColor DarkGray
        continue
    }

    if ($null -eq $entries -or $entries.Count -eq 0) {
        Write-Host "  $($store.Name): empty" -ForegroundColor DarkGray
        continue
    }

    $suspect = @($entries.Keys | Where-Object { -not (Test-IsGenuineClientId $_) } | Sort-Object)
    $genuine = $entries.Count - $suspect.Count

    if ($suspect.Count -eq 0) {
        Write-Host "  $($store.Name): clean ($genuine genuine)" -ForegroundColor Green
        continue
    }

    $totalSuspect += $suspect.Count
    Write-Host "  $($store.Name): $($suspect.Count) fixture-looking $($store.What) entr(y/ies), $genuine genuine" -ForegroundColor Yellow
    foreach ($id in $suspect) {
        Write-Host "      $id"
    }

    if ($WriteProposal) {
        $kept = [ordered] @{}
        foreach ($id in ($entries.Keys | Sort-Object)) {
            if (Test-IsGenuineClientId $id) { $kept[$id] = $entries[$id] }
        }
        if (-not (Test-Path -LiteralPath $ProposalDirectory)) {
            New-Item -ItemType Directory -Path $ProposalDirectory -Force | Out-Null
        }
        Protect-ProposalDirectory $ProposalDirectory

        # Create the file and harden it BEFORE the secrets go into it. Writing first and hardening
        # afterwards leaves a window where every genuine client's reconnect secret is readable at a
        # predictable path, which on a shared Linux box is enough.
        $proposal = Join-Path $ProposalDirectory "$($store.Name).cleaned.json"
        Set-Content -LiteralPath $proposal -Value '' -Encoding utf8
        Protect-ProposalFile $proposal
        $kept | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $proposal -Encoding utf8
        Write-Host "      proposal written: $proposal" -ForegroundColor Cyan
    }
}

Write-Host ''
if ($totalSuspect -eq 0) {
    Write-Host 'No fixture identities found.' -ForegroundColor Green
    exit 0
}

Write-Host "$totalSuspect fixture-looking entr(y/ies) found. Nothing was changed." -ForegroundColor Yellow
Write-Host 'To remove them: stop RemEx, back up the store, check each id above is not a phone of'
Write-Host 'yours, then edit the file (or move the .cleaned.json copy into place) and restart.'
Write-Host 'A wrong deletion unpairs that device and it has to be paired again by PIN.'
Write-Host 'If you move a proposal into place, re-apply the owner-only ACL to it afterwards - a copy'
Write-Host 'inherits the store directory permissions, which are broader than the store''s own.'
exit 0
