param(
  [string] $Runtime = "win-x64",
  [string] $SelfContained = "true",
  [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$selfContainedBool = $SelfContained -match '^(?i:true|1|yes)$'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
$proj = Join-Path $root "RadioDeck.csproj"

$outDir = Join-Path $root "artifacts\\publish\\$Runtime"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Publishing -> $outDir"

dotnet publish $proj `
  -c $Configuration `
  -r $Runtime `
  --self-contained $selfContainedBool `
  -o $outDir `
  --nologo `
  /p:PublishSingleFile=false `
  /p:PublishReadyToRun=false | Out-Host

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Done."
