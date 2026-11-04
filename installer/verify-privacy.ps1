#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
foreach($name in @('AstraLoader.exe','AstraLoader.dll')){
 $file=Join-Path $PublishDirectory $name
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing artifact: $name"}
 $bytes=[IO.File]::ReadAllBytes($file)
 foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)){
  $content=$encoding.GetString($bytes)
  if($content -match '(?i)[a-z]:\\Users\\' -or $content.IndexOf($sourceRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw "Local build path found in $name"}
 }
}
Write-Output 'PASS: personal Windows and project paths are absent from application artifacts.'
