[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit 0
}

$installDir = Join-Path ${env:ProgramFiles} 'BlockGame'
$dataDir = Join-Path ${env:ProgramData} 'BlockGame'
$guardExe = Join-Path $installDir 'BlockGame.Guard.exe'
$tokenFile = Join-Path $dataDir 'uninstall.token'
$maintenanceStopFile = Join-Path $dataDir 'maintenance-stop.request'
$serviceName = 'BlockGameGuard'

$expectedInstall = [IO.Path]::GetFullPath((Join-Path ${env:ProgramFiles} 'BlockGame'))
$expectedData = [IO.Path]::GetFullPath((Join-Path ${env:ProgramData} 'BlockGame'))
if ([IO.Path]::GetFullPath($installDir) -ne $expectedInstall -or [IO.Path]::GetFullPath($dataDir) -ne $expectedData) {
    throw 'Uninstall target path validation failed.'
}

if (-not (Test-Path -LiteralPath $guardExe) -or -not (Test-Path -LiteralPath $tokenFile)) {
    throw 'No valid uninstall authorization found. Generate one in BlockGame settings first.'
}

$token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'The uninstall authorization is empty.'
}

& $guardExe --verify-uninstall-token $token
if ($LASTEXITCODE -ne 0) {
    throw 'The uninstall authorization is invalid, expired, or protection is still locked.'
}

# The management UI may have started the uninstaller and still be holding its
# configuration or audit files open. Stop it before the deferred cleanup runs.
Get-Process -Name 'BlockGame.App' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'BlockGame.App' -ErrorAction SilentlyContinue |
    Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

Set-Content -LiteralPath $maintenanceStopFile -Value ([Guid]::NewGuid().ToString('N')) -Encoding ASCII
try {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
}
finally {
    Remove-Item -LiteralPath $maintenanceStopFile -Force -ErrorAction SilentlyContinue
}
& sc.exe delete $serviceName | Out-Null
Remove-Item -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BlockGame' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs\BlockGame.lnk') -Force -ErrorAction SilentlyContinue

$cleanupScript = Join-Path $env:TEMP ('BlockGameCleanup-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$cleanup = @"
param([string]`$InstallDir, [string]`$DataDir, [string]`$CleanupScript)
Start-Sleep -Seconds 2
`$expectedInstall = [IO.Path]::GetFullPath((Join-Path `$env:ProgramFiles 'BlockGame'))
`$expectedData = [IO.Path]::GetFullPath((Join-Path `$env:ProgramData 'BlockGame'))

function Remove-BlockGameDirectory {
    param(
        [string]`$Path,
        [int]`$MaxAttempts = 30
    )

    for (`$attempt = 1; `$attempt -le `$MaxAttempts; `$attempt++) {
        if (-not (Test-Path -LiteralPath `$Path)) {
            return `$true
        }

        try {
            Remove-Item -LiteralPath `$Path -Recurse -Force -ErrorAction Stop
        }
        catch {
            Start-Sleep -Seconds 1
            continue
        }

        if (-not (Test-Path -LiteralPath `$Path)) {
            return `$true
        }

        Start-Sleep -Seconds 1
    }

    return -not (Test-Path -LiteralPath `$Path)
}

if ([IO.Path]::GetFullPath(`$InstallDir) -eq `$expectedInstall -and (Test-Path -LiteralPath `$InstallDir)) {
    if (-not (Remove-BlockGameDirectory -Path `$InstallDir)) {
        Write-Error "Unable to remove BlockGame installation directory: `$InstallDir"
    }
}
if ([IO.Path]::GetFullPath(`$DataDir) -eq `$expectedData -and (Test-Path -LiteralPath `$DataDir)) {
    if (-not (Remove-BlockGameDirectory -Path `$DataDir)) {
        Write-Error "Unable to remove BlockGame data directory: `$DataDir"
    }
}
Remove-Item -LiteralPath `$CleanupScript -Force -ErrorAction SilentlyContinue
"@
Set-Content -LiteralPath $cleanupScript -Value $cleanup -Encoding UTF8
# Explicit elevation is required here: this helper removes files from Program Files
# after the uninstaller executable has exited, so it cannot rely on its parent token.
Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $cleanupScript,
    '-InstallDir', $installDir,
    '-DataDir', $dataDir,
    '-CleanupScript', $cleanupScript
)
Write-Host 'BlockGame uninstall authorized; cleanup will finish after the service exits.'
