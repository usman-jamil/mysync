# Installs mysync on Windows (README "Install"). After this, `mysync update` keeps it current.
#
#   irm https://raw.githubusercontent.com/usman-jamil/mysync/main/install.ps1 | iex
#
# Downloads the release archive for this machine, checks that SHA256SUMS.txt is signed with the
# mysync release key and that the archive matches it, and installs mysync.exe into
# $env:MYSYNC_INSTALL_DIR (default: %LOCALAPPDATA%\Programs\mysync, no administrator needed), adding
# it to your user PATH. Nothing is installed if any check fails. Works in Windows PowerShell 5.1 and PowerShell 7.
#
# Settings (all optional): MYSYNC_VERSION, MYSYNC_INSTALL_DIR, GITHUB_TOKEN.
# For testing only: MYSYNC_INSTALL_SOURCE (a local release folder), MYSYNC_INSTALL_PUBLIC_KEY_FILE.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

# A fork that hosts its own copy of this script changes these two: its repository, and the public
# half of its MYSYNC_RELEASE_SIGNING_KEY (CLAUDE.md §16).
$Repository = 'usman-jamil/mysync'
$ReleaseKey = @'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXY20aDASj1RWM+Vq4jdThyocT2Ge
mY1VkAKYECLHhDwSH413cffvkt9T14jYf8QX8FJIM/+WCXjHdtV/wudqEg==
-----END PUBLIC KEY-----
'@

function Fail([string] $Message) { throw "mysync install: $Message" }

# ECDSA P-256 over SHA-256, DER signature (what `openssl dgst -sha256 -sign` writes).
# PowerShell 7 has ImportSubjectPublicKeyInfo; Windows PowerShell 5.1 (.NET Framework) goes through CNG.
function Test-ReleaseSignature([byte[]] $Data, [byte[]] $DerSignature, [string] $PublicKeyPem) {
    $spki = [Convert]::FromBase64String((($PublicKeyPem -split "`n") | Where-Object { $_ -notmatch '-----' }) -join '')
    $raw = ConvertFrom-DerSignature $DerSignature
    if ($null -eq $raw) { return $false }
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash($Data)

    $ecdsa = [Security.Cryptography.ECDsa]::Create()
    if ($ecdsa.PSObject.Methods.Name -contains 'ImportSubjectPublicKeyInfo') {
        [int]$read = 0
        $ecdsa.ImportSubjectPublicKeyInfo([byte[]]$spki, [ref]$read)
        return $ecdsa.VerifyHash($hash, $raw)
    }

    # SubjectPublicKeyInfo for P-256 ends with 0x04 || X(32) || Y(32).
    $point = $spki[($spki.Length - 64)..($spki.Length - 1)]
    $blob = [byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0x00, 0x00, 0x00) + $point   # BCRYPT_ECDSA_PUBLIC_P256_MAGIC, key length 32
    $key = [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $cng = New-Object Security.Cryptography.ECDsaCng($key)
    return $cng.VerifyHash($hash, $raw)
}

# DER SEQUENCE { INTEGER r, INTEGER s } → r || s, each 32 bytes.
function ConvertFrom-DerSignature([byte[]] $Der) {
    try {
        if ($Der[0] -ne 0x30) { return $null }
        $offset = 2
        if ($Der[1] -band 0x80) { $offset += $Der[1] -band 0x7f }
        $parts = @()
        for ($i = 0; $i -lt 2; $i++) {
            if ($Der[$offset] -ne 0x02) { return $null }
            $length = $Der[$offset + 1]
            $value = $Der[($offset + 2)..($offset + 1 + $length)]
            while ($value.Count -gt 32 -and $value[0] -eq 0) { $value = $value[1..($value.Count - 1)] }
            if ($value.Count -gt 32) { return $null }
            $parts += , ([byte[]](@(0) * (32 - $value.Count)) + [byte[]]$value)
            $offset += 2 + $length
        }
        return [byte[]]($parts[0] + $parts[1])
    }
    catch { return $null }
}

# Tests load the functions above without installing anything.
if ($env:MYSYNC_INSTALL_FUNCTIONS_ONLY) { return }

$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$rid = switch ($arch) { 'AMD64' { 'win-x64' } 'ARM64' { 'win-arm64' } default { Fail "unsupported processor $arch" } }
$installDir = if ($env:MYSYNC_INSTALL_DIR) { $env:MYSYNC_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\mysync' }
$headers = @{ 'User-Agent' = 'mysync-install' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$work = Join-Path ([IO.Path]::GetTempPath()) ("mysync-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $version = $env:MYSYNC_VERSION
    if ($env:MYSYNC_INSTALL_SOURCE) {
        if (-not $version) {
            $version = Get-ChildItem -Directory $env:MYSYNC_INSTALL_SOURCE |
                Where-Object Name -match '^mysync-(\d+\.\d+\.\d+)$' |
                ForEach-Object { [version]$Matches[1] } | Sort-Object | Select-Object -Last 1
            if (-not $version) { Fail "no release found in $($env:MYSYNC_INSTALL_SOURCE)" }
            $version = "$version"
        }
        function Get-Asset([string] $Name) {
            $from = Join-Path $env:MYSYNC_INSTALL_SOURCE "mysync-$version\$Name"
            if (-not (Test-Path $from)) { Fail "$Name is missing from mysync $version" }
            Copy-Item $from (Join-Path $work $Name)
        }
    }
    else {
        if (-not $version) {
            $latest = Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$Repository/releases/latest"
            $version = $latest.tag_name -replace '^mysync-v?', ''
        }
        function Get-Asset([string] $Name) {
            try { Invoke-WebRequest -UseBasicParsing -Headers $headers -OutFile (Join-Path $work $Name) "https://github.com/$Repository/releases/download/mysync-$version/$Name" }
            catch { Fail "$Name is missing from mysync $version" }
        }
    }

    $archive = "mysync-$version-$rid.zip"
    Write-Host "Installing mysync $version ($rid) into $installDir"
    Get-Asset 'SHA256SUMS.txt'
    Get-Asset 'SHA256SUMS.txt.sig'
    Get-Asset $archive

    $keyPem = if ($env:MYSYNC_INSTALL_PUBLIC_KEY_FILE) { Get-Content -Raw $env:MYSYNC_INSTALL_PUBLIC_KEY_FILE } else { $ReleaseKey }
    $sums = [IO.File]::ReadAllBytes((Join-Path $work 'SHA256SUMS.txt'))
    $signature = [IO.File]::ReadAllBytes((Join-Path $work 'SHA256SUMS.txt.sig'))
    if (-not (Test-ReleaseSignature $sums $signature $keyPem)) { Fail 'SHA256SUMS.txt is not signed with the mysync release key; nothing was installed' }

    $expected = Get-Content (Join-Path $work 'SHA256SUMS.txt') |
        ForEach-Object { $parts = $_ -split '\s+', 2; if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $archive) { $parts[0] } } |
        Select-Object -First 1
    if (-not $expected) { Fail "$archive is not listed in SHA256SUMS.txt" }
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $work $archive)).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) { Fail "$archive does not match its checksum; nothing was installed" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $work 'unpacked'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $work $archive), $unpacked)
    $exe = Join-Path $unpacked 'mysync.exe'
    if (-not (Test-Path $exe)) { Fail "$archive doesn't contain mysync.exe" }

    $reported = (& $exe --version | Out-String).Trim()
    if ($reported -ne $version) { Fail "the downloaded mysync reports version $reported, expected $version" }
    & $exe self-check | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'the downloaded mysync failed its self-check' }

    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Move-Item -Force $exe (Join-Path $installDir 'mysync.exe')

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (($userPath -split ';') -contains $installDir)) {
        [Environment]::SetEnvironmentVariable('Path', (($userPath, $installDir) -ne '' -join ';').Trim(';'), 'User')
        Write-Host "Added $installDir to your user PATH; open a new terminal to use it."
    }

    Write-Host "Installed: mysync $version at $(Join-Path $installDir 'mysync.exe')"
    Write-Host 'Keep it current with: mysync update'
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
