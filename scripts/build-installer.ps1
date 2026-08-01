[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version = '0.1.1'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$buildScript = Join-Path $PSScriptRoot 'build.ps1'
$installerScript = Join-Path $repoRoot 'installer\BlockGame.iss'
$releaseDirectory = Join-Path $repoRoot 'artifacts\release'
$setupPath = Join-Path $releaseDirectory 'BlockGame-Setup.exe'
$checksumPath = Join-Path $releaseDirectory 'BlockGame-Setup.exe.sha256'

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $knownPaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )

    foreach ($path in $knownPaths) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
            return $path
        }
    }

    throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup --exact'
}

& $buildScript -SelfContained -Version $Version
if ($LASTEXITCODE -ne 0) {
    throw 'Self-contained application build failed.'
}

New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue

$compiler = Find-InnoCompiler
& $compiler "/DMyAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw 'Installer build failed.'
}

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Installer output was not created: $setupPath"
}

$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
('{0}  {1}' -f $hash.Hash, [IO.Path]::GetFileName($setupPath)) |
    Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Installer complete: $setupPath"
Write-Host "SHA256: $($hash.Hash)"
