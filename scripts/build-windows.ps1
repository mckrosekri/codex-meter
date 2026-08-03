[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.5.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'windows\CodexMeterTray\CodexMeterTray.csproj'
$publishDir = Join-Path $root 'artifacts\windows\win-x64'
$installerDir = Join-Path $root 'artifacts\installer'
$installerScript = Join-Path $root 'installer\CodexMeter.iss'

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir, $installerDir -Force | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:Version=$Version `
    -p:PublishDir="$publishDir\" `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false

if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

if ($SkipInstaller) {
    Write-Host "Published Codex Decision to $publishDir"
    return
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$installerDir" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $installerDir "CodexDecisionSetup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Expected installer was not created: $installer"
}

Write-Host "Created $installer"
