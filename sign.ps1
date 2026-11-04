#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$CertificateDirectory,
    [System.Security.SecureString]$PfxPassword,
    [string]$SignToolPath,
    [uri]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SignAllBinaries,
    [string[]]$FileNames = @('AstraLoader.exe', 'AstraLoader.dll')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) { throw 'Cannot determine the signing script directory.' }
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    if (Test-Path -LiteralPath (Join-Path $scriptDirectory 'AstraLoader.exe') -PathType Leaf) { $PublishDirectory = $scriptDirectory }
    else { $PublishDirectory = Join-Path $scriptDirectory 'publish' }
}
if ([string]::IsNullOrWhiteSpace($CertificateDirectory)) {
    $CertificateDirectory = Join-Path $scriptDirectory 'certificates'
    $legacyCertificates = Join-Path (Split-Path -Parent $scriptDirectory) 'NL-Cleaner\certificates'
    if (-not (Test-Path -LiteralPath (Join-Path $CertificateDirectory 'Neverlose-fixed.pfx')) -and
        (Test-Path -LiteralPath (Join-Path $legacyCertificates 'Neverlose-fixed.pfx'))) {
        $CertificateDirectory = $legacyCertificates
    }
}

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) { throw 'Build the application first (build.bat).' }
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
# Sign only project-owned assemblies by default. --self-contained includes already-signed Microsoft runtime files.
$files = @(Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse | Where-Object {
    $_.Extension -in '.exe', '.dll' -and ($SignAllBinaries -or $_.Name -in $FileNames)
})
if ($files.Count -eq 0) { throw 'No matching EXE or DLL files were found.' }
if (-not $SignToolPath) {
    $installed = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($installed) { $SignToolPath = $installed.Source }
    else {
        $kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        if (Test-Path -LiteralPath $kitRoot) {
            $versions = @(Get-ChildItem -LiteralPath $kitRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending)
            foreach ($version in $versions) {
                $candidate = Join-Path $version.FullName 'x64\signtool.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $SignToolPath = $candidate; break }
            }
        }
    }
}
if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
    throw 'Install Windows SDK Signing Tools, or specify -SignToolPath.'
}
if ($TimestampUrl.Scheme -notin 'http', 'https') { throw 'TimestampUrl must be HTTP or HTTPS.' }
New-Item -ItemType Directory -Force -Path $CertificateDirectory | Out-Null
$CertificateDirectory = (Resolve-Path -LiteralPath $CertificateDirectory).Path
$pfxPath = Join-Path $CertificateDirectory 'Neverlose-fixed.pfx'
$cerPath = Join-Path $CertificateDirectory 'Neverlose-fixed.cer'
if (-not $PfxPassword) { $PfxPassword = Read-Host 'Password for exported PFX' -AsSecureString }
if ($PfxPassword.Length -lt 1) { throw 'A nonempty PFX password is required.' }

if (Test-Path -LiteralPath $pfxPath -PathType Leaf) {
    $cert = Import-PfxCertificate -FilePath $pfxPath -Password $PfxPassword -CertStoreLocation 'Cert:\CurrentUser\My' -Exportable
    if ($cert.Subject -ne 'CN=Neverlose fixed' -or -not $cert.HasPrivateKey -or $cert.NotAfter -le (Get-Date)) {
        throw 'Existing PFX must contain a current Neverlose fixed certificate with its private key.'
    }
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Neverlose fixed' `
        -FriendlyName 'Neverlose fixed' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(2)
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $PfxPassword | Out-Null
}
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

if (-not ('NLCleaner.SignatureVerifier' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace NLCleaner {
 public static class SignatureVerifier {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct FileInfo { public uint Size; public string Path; public IntPtr Handle, Subject; }
  [StructLayout(LayoutKind.Sequential)]
  struct TrustData {
   public uint Size; public IntPtr Callback, Client; public uint UI, Revocation, Choice;
   public IntPtr File; public uint StateAction; public IntPtr State, URL;
   public uint Flags, Context; public IntPtr Settings;
  }
  [DllImport("wintrust.dll", ExactSpelling=true)]
  static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
  public static int Verify(string path) {
   var file = new FileInfo { Size=(uint)Marshal.SizeOf(typeof(FileInfo)), Path=path };
   var pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
   Marshal.StructureToPtr(file,pointer,false);
   var data=new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)), UI=2, Choice=1, File=pointer, StateAction=1 };
   var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
   try { return WinVerifyTrust(new IntPtr(-1),ref action,ref data); }
   finally {
    data.StateAction=2; WinVerifyTrust(new IntPtr(-1),ref action,ref data);
    Marshal.DestroyStructure(pointer,typeof(FileInfo)); Marshal.FreeHGlobal(pointer);
   }
  }
 }
}
"@
}
# Select the same certificate in CurrentUser\My instead of exposing the PFX password in a command line.
foreach ($file in $files) {
    & $SignToolPath sign /sha1 $cert.Thumbprint /s My /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 /d 'Astra Loader' $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing or timestamping failed: $($file.FullName) (exit $LASTEXITCODE)" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        throw "Signer mismatch: $($file.FullName)"
    }
    if (-not $signature.TimeStamperCertificate) { throw "No timestamp found: $($file.FullName)" }
    $trustCode = [NLCleaner.SignatureVerifier]::Verify($file.FullName)
    # CERT_E_UNTRUSTEDROOT is expected for this explicitly self-signed identity.
    # Do not accept other UnknownError results, invalid digests, or missing signatures.
    if ($trustCode -ne 0 -and $trustCode -ne -2146762487) {
        throw ('Authenticode verification failed: 0x{0:X8}; {1}' -f $trustCode, $signature.StatusMessage)
    }
    if ($trustCode -eq -2146762487) { Write-Host 'Signature present; certificate root is self-signed and untrusted.' }
    Write-Host "Signed and timestamped: $($file.Name) [$($signature.Status)]"
}
Write-Host "Certificate subject: $($cert.Subject)"
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "PFX: $pfxPath"
Write-Host "CER: $cerPath"
Write-Host 'The self-signed certificate is not automatically trusted. No trust-store changes were made.'
