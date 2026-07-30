[CmdletBinding()]
param(
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-QuotedProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    # Windows paths cannot contain a double quote. Rejecting one here keeps the
    # command line unambiguous when Windows PowerShell joins ArgumentList items.
    if ($Value.Contains('"')) {
        throw 'A cleanup process argument contains an invalid double quote.'
    }

    return '"' + $Value + '"'
}

if (-not (Test-IsAdministrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -WaitForProcessId {1}' -f `
        $PSCommandPath, $WaitForProcessId
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit 0
}

$nativeProgramFiles = if ([string]::IsNullOrWhiteSpace($env:ProgramW6432)) {
    $env:ProgramFiles
}
else {
    $env:ProgramW6432
}
$installDir = Join-Path $nativeProgramFiles 'BlockGame'
$dataDir = Join-Path ${env:ProgramData} 'BlockGame'
$guardExe = Join-Path $installDir 'BlockGame.Guard.exe'
$tokenFile = Join-Path $dataDir 'uninstall.token'
$maintenanceStopFile = Join-Path $dataDir 'maintenance-stop.request'
$serviceName = 'BlockGameGuard'

$expectedInstall = [IO.Path]::GetFullPath((Join-Path $nativeProgramFiles 'BlockGame'))
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
$uninstallShortcutName = (-join @([char]0x5378, [char]0x8F7D, ' BlockGame.lnk'))
Remove-Item -LiteralPath (Join-Path ${env:ProgramData} "Microsoft\Windows\Start Menu\Programs\$uninstallShortcutName") -Force -ErrorAction SilentlyContinue

$cleanupScript = Join-Path $env:TEMP ('BlockGameCleanup-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$cleanup = @"
param(
    [string]`$InstallDir,
    [string]`$DataDir,
    [string]`$CleanupScript,
    [int]`$WaitForProcessId
)
# The uninstaller is normally launched with the installation directory as its
# working directory. Leave it before deletion so this helper does not lock the
# directory that it is responsible for removing.
Set-Location -LiteralPath `$env:SystemRoot
if (`$WaitForProcessId -gt 0) {
    Wait-Process -Id `$WaitForProcessId -Timeout 30 -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1
`$nativeProgramFiles = if ([string]::IsNullOrWhiteSpace(`$env:ProgramW6432)) {
    `$env:ProgramFiles
}
else {
    `$env:ProgramW6432
}
`$expectedInstall = [IO.Path]::GetFullPath((Join-Path `$nativeProgramFiles 'BlockGame'))
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
# Windows PowerShell joins ArgumentList array items into one command line. Preserve
# path boundaries explicitly so "C:\Program Files\BlockGame" remains one argument.
Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -WorkingDirectory $env:SystemRoot -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', (ConvertTo-QuotedProcessArgument $cleanupScript),
    '-InstallDir', (ConvertTo-QuotedProcessArgument $installDir),
    '-DataDir', (ConvertTo-QuotedProcessArgument $dataDir),
    '-CleanupScript', (ConvertTo-QuotedProcessArgument $cleanupScript),
    '-WaitForProcessId', $WaitForProcessId
)
Write-Host 'BlockGame uninstall authorized; cleanup will finish after the service exits.'
