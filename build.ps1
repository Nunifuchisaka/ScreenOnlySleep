# Builds the Release exe, runs the unit tests and packs dist\ScreenOnlySleep-<version>.zip
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-SkipTests]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not $SkipTests) {
  dotnet test tests\ScreenOnlySleep.Tests\ScreenOnlySleep.Tests.csproj -c Release
  if ($LASTEXITCODE -ne 0) { throw 'unit tests failed' }
}

dotnet build src\ScreenOnlySleep\ScreenOnlySleep.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$out = Join-Path $root 'src\ScreenOnlySleep\bin\Release\net48'
$exe = Join-Path $out 'ScreenOnlySleep.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "ScreenOnlySleep-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $exe, (Join-Path $out 'ScreenOnlySleep.exe.config') $stage
Copy-Item README.md, README.ja.md, LICENSE $stage

$zip = Join-Path $dist "ScreenOnlySleep-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Get-Item $exe, $zip | Select-Object FullName, Length | Format-Table -AutoSize
