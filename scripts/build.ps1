[CmdletBinding()]
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$buildHome = Join-Path $repoRoot '.build-appdata'
$dotnetHome = Join-Path $repoRoot '.dotnet-cli'
$nugetPackages = Join-Path $repoRoot '.nuget\packages'
$publishRoot = Join-Path $repoRoot 'artifacts\publish'

$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = $nugetPackages
$env:APPDATA = $buildHome

New-Item -ItemType Directory -Force -Path $buildHome, $dotnetHome, $nugetPackages | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

if (Test-Path -LiteralPath $publishRoot) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishRoot).Path
    $resolvedArtifacts = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'artifacts')).Path
    if (-not $resolvedPublish.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

if ($SelfContained) {
    dotnet restore (Join-Path $repoRoot 'BlockGame.sln') `
        -r win-x64 `
        --configfile (Join-Path $repoRoot 'NuGet.Config') `
        --source 'https://api.nuget.org/v3/index.json'
} else {
    dotnet restore (Join-Path $repoRoot 'BlockGame.sln') --configfile (Join-Path $repoRoot 'NuGet.Config')
}
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet restore failed.'
}

$commonArguments = @(
    '-c', 'Release',
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '--no-restore',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
if ($SelfContained) {
    $commonArguments += @('-r', 'win-x64')
}

dotnet publish (Join-Path $repoRoot 'src\BlockGame.Guard\BlockGame.Guard.csproj') @commonArguments -o (Join-Path $publishRoot 'guard')
if ($LASTEXITCODE -ne 0) { throw 'Guard publish failed.' }
dotnet publish (Join-Path $repoRoot 'src\BlockGame.App\BlockGame.App.csproj') @commonArguments -o (Join-Path $publishRoot 'app')
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $repoRoot 'src\BlockGame.Uninstall\BlockGame.Uninstall.csproj') @commonArguments -o (Join-Path $publishRoot 'uninstall')
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller publish failed.' }

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall-installed.ps1') -Destination (Join-Path $publishRoot 'uninstall-installed.ps1') -Force
Write-Host "Build complete: $publishRoot"
if (-not $SelfContained) {
    Write-Host "Note: framework-dependent publish requires the .NET 9 Desktop Runtime on the target PC."
}
