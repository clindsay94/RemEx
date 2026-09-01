<#
.SYNOPSIS
    Re-applies a signed uiAccess="true" manifest to an installed Remex.Agent apphost.

.DESCRIPTION
    Remote control of a Windows UAC prompt needs two things on the host:

      1. The prompt must render on the interactive desktop, not the Winlogon secure
         desktop  ->  HKLM ...\Policies\System\PromptOnSecureDesktop = 0  (machine
         policy; NOT set here, it is a security decision left to the operator).
      2. The agent must be allowed to inject input into the System-integrity consent
         window  ->  uiAccess="true" in the manifest, which Windows only honours when
         the binary is Authenticode-signed AND lives in a secure path (Program Files).

    The published apphost ships unsigned with uiAccess="false", so a plain
    `dotnet publish` + copy (see update-local-install.ps1 / the installer) reverts the
    privilege on every update. This script re-applies it to a given exe: it ensures a
    trusted code-signing cert exists, flips the embedded manifest to uiAccess="true",
    and signs the file. Idempotent — safe to run on every deploy. (RemEx-ywl7o)

    For a public release the self-signed cert is the wrong trust anchor (end-user
    machines would not trust it, so the app would refuse to launch). Pass -CertSubject
    / rely on a real code-signing cert already in LocalMachine\My for that case.

.PARAMETER ExePath
    The apphost to process, e.g. "C:\Program Files\RemEx\Remex.Agent.exe".

.PARAMETER CertSubject
    Subject of the code-signing cert to use/create in Cert:\LocalMachine\My.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ExePath,
    [string]$CertSubject = 'CN=RemEx Dev Code Signing'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "Exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

# --- locate mt.exe (latest Windows SDK) ---------------------------------------
$mt = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter mt.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\mt.exe$' } |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $mt) { throw 'mt.exe not found — install the Windows 10/11 SDK (Windows Kits\10\bin\<ver>\x64\mt.exe).' }

# --- ensure a trusted code-signing cert ---------------------------------------
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating code-signing cert $CertSubject ..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
        -CertStoreLocation Cert:\LocalMachine\My -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'RemEx Dev Code Signing'
}
# Trust it: Root = chain validity, TrustedPublisher = uiAccess acceptance.
$cerTmp = Join-Path $env:TEMP ("remex-signing-" + $cert.Thumbprint + '.cer')
Export-Certificate -Cert $cert -FilePath $cerTmp -Force | Out-Null
foreach ($store in @('Root', 'TrustedPublisher')) {
    if (-not (Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
        Import-Certificate -FilePath $cerTmp -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        Write-Host "Trusted cert in LocalMachine\$store" -ForegroundColor DarkGray
    }
}
Remove-Item $cerTmp -ErrorAction SilentlyContinue

# --- flip embedded manifest to uiAccess=true ----------------------------------
$maniPath = Join-Path $env:TEMP ('remex-embedded-' + [guid]::NewGuid().ToString('N') + '.manifest')
& $mt -nologo -inputresource:"$ExePath;#1" -out:"$maniPath" 2>&1 | Out-Null
if (-not (Test-Path $maniPath)) { throw "Could not extract embedded manifest from $ExePath" }
$mani = Get-Content $maniPath -Raw

if ($mani -match 'uiAccess="false"') {
    $mani = $mani -replace 'uiAccess="false"', 'uiAccess="true"'
} elseif ($mani -match 'uiAccess="true"') {
    # already set — still re-sign below in case the copy replaced the file
} elseif ($mani -match 'requestedExecutionLevel level="[^"]*"') {
    $mani = $mani -replace '(requestedExecutionLevel level="[^"]*")', '$1 uiAccess="true"'
} else {
    throw 'No <requestedExecutionLevel> in the embedded manifest — cannot set uiAccess.'
}
Set-Content -Path $maniPath -Value $mani -Encoding UTF8
& $mt -nologo -manifest "$maniPath" -outputresource:"$ExePath;#1"
if ($LASTEXITCODE -ne 0) { throw "mt.exe failed to embed manifest (exit $LASTEXITCODE) — is $ExePath still running/locked?" }
Remove-Item $maniPath -ErrorAction SilentlyContinue

# --- sign + verify ------------------------------------------------------------
$sig = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid') { throw "Signing failed: $($sig.Status) — $($sig.StatusMessage)" }

$verifyMani = Join-Path $env:TEMP ('remex-verify-' + [guid]::NewGuid().ToString('N') + '.manifest')
& $mt -nologo -inputresource:"$ExePath;#1" -out:"$verifyMani" 2>&1 | Out-Null
$finalUi = ([regex]::Match((Get-Content $verifyMani -Raw), 'uiAccess="[^"]*"')).Value
Remove-Item $verifyMani -ErrorAction SilentlyContinue
if ($finalUi -ne 'uiAccess="true"') { throw "Post-check: embedded manifest is '$finalUi', expected uiAccess=true." }

Write-Host "uiAccess=true + signed ($($cert.Thumbprint)) -> $ExePath" -ForegroundColor Green
