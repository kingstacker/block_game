[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:BLOCKGAME_DATA_DIR = Join-Path $repoRoot '.dev-data'
$guardDll = Join-Path $repoRoot 'src\BlockGame.Guard\bin\Debug\net9.0-windows\BlockGame.Guard.dll'
if (-not (Test-Path -LiteralPath $guardDll)) {
    throw 'Build BlockGame.Guard first.'
}
dotnet $guardDll --console
