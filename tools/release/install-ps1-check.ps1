# Checks install.ps1's signature verification against fixtures signed with a test-only key
# (tools/release/testdata; never the release key). CI runs it with Windows PowerShell 5.1, whose
# .NET Framework path (CNG) can't be run anywhere else; PowerShell 7 takes the other path.
#
#   powershell -NoProfile -File tools/release/install-ps1-check.ps1     # Windows PowerShell 5.1
#   pwsh -NoProfile -File tools/release/install-ps1-check.ps1           # PowerShell 7, any OS

$ErrorActionPreference = 'Stop'
$env:MYSYNC_INSTALL_FUNCTIONS_ONLY = '1'
. (Join-Path $PSScriptRoot '..\..\install.ps1')

$data = Join-Path $PSScriptRoot 'testdata'
$sums = [IO.File]::ReadAllBytes((Join-Path $data 'SHA256SUMS.txt'))
$key = Get-Content -Raw (Join-Path $data 'test-only.pub.pem')
$tampered = [Text.Encoding]::UTF8.GetBytes("abd  mysync-1.0.0-win-x64.zip`n")
$failures = 0

function Expect([string] $What, [bool] $Actual, [bool] $Expected) {
    if ($Actual -eq $Expected) { Write-Host "ok   $What" }
    else { Write-Host "FAIL $What (got $Actual, expected $Expected)"; $script:failures++ }
}

foreach ($signature in Get-ChildItem (Join-Path $data 'valid-*.sig')) {
    $bytes = [IO.File]::ReadAllBytes($signature.FullName)
    Expect "$($signature.Name) ($($bytes.Length) bytes) verifies" (Test-ReleaseSignature $sums $bytes $key) $true
    Expect "$($signature.Name) does not verify tampered checksums" (Test-ReleaseSignature $tampered $bytes $key) $false
}

Expect 'a signature by another key is refused' (Test-ReleaseSignature $sums ([IO.File]::ReadAllBytes((Join-Path $data 'other-key.sig'))) $key) $false
Expect 'garbage is refused' (Test-ReleaseSignature $sums ([byte[]](0x30, 0x02, 0x01)) $key) $false

Write-Host "PowerShell $($PSVersionTable.PSVersion): $failures failure(s)"
if ($failures -gt 0) { exit 1 }
