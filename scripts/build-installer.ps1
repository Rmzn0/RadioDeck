param(
  [ValidateSet("win-x64")]
  [string] $Runtime = "win-x64",
  # Full path of ISCC.exe when Inno Setup is not in a standard location.
  [string] $IsccPath,
  # Folder of a .NET 8 SDK (contains dotnet.exe) when none is on the PATH.
  [string] $DotnetDir
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent

$iss = Join-Path $root "installer\\RadioDeck.iss"
if (!(Test-Path $iss)) {
  throw "Missing installer script: $iss"
}

function Find-Iscc {
  if ($IsccPath) {
    if (!(Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw "Compiler not found: $IsccPath" }
    return (Resolve-Path -LiteralPath $IsccPath).Path
  }
  $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }
  $candidates = @(
    "${env:ProgramFiles(x86)}\\Inno Setup 6\\ISCC.exe",
    "${env:ProgramFiles}\\Inno Setup 6\\ISCC.exe",
    "${env:LOCALAPPDATA}\\Programs\\Inno Setup 6\\ISCC.exe"
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
  return $null
}

$iscc = Find-Iscc
if ($null -eq $iscc) {
  Write-Host "Inno Setup not found. Install Inno Setup 6, then compile:" -ForegroundColor Yellow
  Write-Host "  $iss" -ForegroundColor Yellow
  exit 2
}

# Validate tools before replacing the previous published output.
if ($DotnetDir) {
  if (!(Test-Path -LiteralPath (Join-Path $DotnetDir 'dotnet.exe'))) { throw "dotnet.exe not found in: $DotnetDir" }
  $env:PATH = "$DotnetDir;$env:PATH"
  $env:DOTNET_ROOT = $DotnetDir
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue) -or !(dotnet --list-sdks)) {
  throw "Install the .NET 8 SDK (the runtime alone cannot build), or pass -DotnetDir."
}
dotnet run --project (Join-Path $root 'tests\PttRegression\PttRegression.csproj') -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Regression checks failed; installer was not updated." }
& (Join-Path $root "scripts\\obfuscate.ps1") -Runtime $Runtime | Out-Host

$output = Join-Path $root "artifacts\obf\$Runtime"
$appDll = Join-Path $output 'RadioDeck.dll'
# Exercise the actual protected DLL, not just the unprotected build, before packaging.
$testOutput = Join-Path $root 'tests\PttRegression\bin\Release\net8.0-windows10.0.17763.0'
$stagedTest = Join-Path $output 'PttRegression.dll'
if (Test-Path -LiteralPath $stagedTest) { throw "Unexpected staged test file: $stagedTest" }
try {
  Copy-Item -LiteralPath (Join-Path $testOutput 'PttRegression.dll') -Destination $stagedTest
  dotnet exec --runtimeconfig (Join-Path $testOutput 'PttRegression.runtimeconfig.json') `
    --depsfile (Join-Path $testOutput 'PttRegression.deps.json') $stagedTest | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Protected regression checks failed; installer was not updated." }
}
finally {
  Remove-Item -LiteralPath $stagedTest -Force -ErrorAction SilentlyContinue
}
$version = (Get-Item -LiteralPath (Join-Path $output 'RadioDeck.exe')).VersionInfo.ProductVersion
$hash = (Get-FileHash -LiteralPath $appDll -Algorithm SHA256).Hash
@(
  "RadioDeck $version",
  "Built: $(Get-Date -Format o)",
  "Runtime: $Runtime (self-contained)",
  'Protection: Obfuscar (application assembly only)',
  "Application DLL SHA256: $hash",
  'PTT/media/call lifecycle regression checks: passed against normal and protected DLLs',
  'Over-air audio/hardware acceptance: not performed'
) | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.txt') -Encoding UTF8

Write-Host "Building installer with: $iscc"
& $iscc $iss | Out-Host

if ($LASTEXITCODE -ne 0) {
  throw "Inno Setup failed with exit code $LASTEXITCODE"
}

Write-Host "Done. Check artifacts\\installer"
