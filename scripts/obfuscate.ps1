param(
  [string] $Runtime = "win-x64",
  [string] $Configuration = "Release",
  [string] $SelfContained = "true"
)

$ErrorActionPreference = "Stop"

$selfContainedBool = $SelfContained -match '^(?i:true|1|yes)$'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent

$publishDir = Join-Path $root "artifacts\\publish\\$Runtime"
$obfDir = Join-Path $root "artifacts\\obf\\$Runtime"

# Refuse path traversal before clearing generated output.
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($publishDir, $obfDir)) {
  $fullPath = [IO.Path]::GetFullPath($target)
  if (!$fullPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
      $Runtime -notmatch '^win-(x64|x86|arm64)$') {
    throw "Invalid publish output path: $target"
  }
}

if (Test-Path $publishDir) {
  Remove-Item -LiteralPath $publishDir -Recurse -Force
}

& (Join-Path $root "scripts\\publish.ps1") -Runtime $Runtime -Configuration $Configuration -SelfContained $SelfContained

$publishAsm = Join-Path $publishDir "RadioDeck.dll"
if (!(Test-Path $publishAsm)) {
  throw "Publish output not found: $publishAsm"
}

if (Test-Path $obfDir) {
  Remove-Item -LiteralPath $obfDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $obfDir | Out-Null

Write-Host "Copy publish -> obf"
Copy-Item -Path (Join-Path $publishDir "*") -Destination $obfDir -Recurse -Force

$cfgPath = Join-Path $env:TEMP ("obfuscar_" + [guid]::NewGuid().ToString("N") + ".xml")

$xml = @"
<?xml version="1.0"?>
<Obfuscator>
  <!-- Obfuscate the published app assembly; third-party/runtime files are copied unchanged. -->
  <Var name="InPath" value="$publishDir" />
  <Var name="OutPath" value="$obfDir" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="RenameFields" value="true" />
  <Var name="RenameMethods" value="true" />
  <Var name="UseUnicodeNames" value="false" />
  <Var name="SuppressIldasm" value="true" />

  <!-- Only obfuscate our app assembly; do NOT obfuscate third-party libs. -->
  <Module file="$publishAsm" />
</Obfuscator>
"@

Set-Content -Path $cfgPath -Value $xml -Encoding UTF8

Write-Host "Obfuscating (output in $obfDir)"
Push-Location $root
try {
  dotnet tool restore | Out-Host
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool restore failed with exit code $LASTEXITCODE"
  }

  dotnet tool run obfuscar.console $cfgPath | Out-Host
  if ($LASTEXITCODE -ne 0) {
    throw "Obfuscar failed with exit code $LASTEXITCODE"
  }
}
finally {
  Pop-Location
}

Remove-Item -Force $cfgPath
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $obfDir "*.pdb")
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $obfDir "Mapping.txt")

Write-Host "Done."
