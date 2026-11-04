#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$IsccPath,
    [string]$BuildRoot,
    [string]$OutputDirectory,
    [switch]$OfflineBuild,
    [switch]$Sign,
    [string]$CertificateDirectory,
    [System.Security.SecureString]$PfxPassword
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $projectRoot 'work\installer-build' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if (-not $base) { continue }
            foreach ($version in @('7', '6')) {
                $candidate = Join-Path $base "Inno Setup $version\ISCC.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate; break }
            }
            if ($IsccPath) { break }
        }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Install Inno Setup 6.7+ or specify -IsccPath.' }
New-Item -ItemType Directory -Force -Path $BuildRoot, $OutputDirectory | Out-Null
$payload = Join-Path $BuildRoot 'payload'
$arguments = @('publish', (Join-Path $projectRoot 'AstraLoader.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--artifacts-path', (Join-Path $BuildRoot 'artifacts'), '-o', $payload)
if ($OfflineBuild) { $arguments += @('-p:NuGetAudit=false', '--ignore-failed-sources') }
& $DotnetPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
& (Join-Path $PSScriptRoot 'verify-privacy.ps1') -PublishDirectory $payload
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $payload 'README.md')
if ($Sign -and -not $PfxPassword) { $PfxPassword = Read-Host 'Password for exported PFX' -AsSecureString }
$signArguments = @{}
if ($CertificateDirectory) { $signArguments.CertificateDirectory = $CertificateDirectory }
if ($PfxPassword) { $signArguments.PfxPassword = $PfxPassword }
if ($Sign) { & (Join-Path $projectRoot 'sign.ps1') -PublishDirectory $payload @signArguments }
& $IsccPath ("/DPayloadDir=" + $payload) ("/DOutputRoot=" + $OutputDirectory) (Join-Path $PSScriptRoot 'AstraLoader.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
if ($Sign) { & (Join-Path $projectRoot 'sign.ps1') -PublishDirectory $OutputDirectory -FileNames 'AstraLoader-Setup.exe' @signArguments }
Write-Host ('Installer ready: ' + (Join-Path $OutputDirectory 'AstraLoader-Setup.exe'))
