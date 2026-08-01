[CmdletBinding()]
param(
    [string]$PublishRoot = '',
    [string]$Version = '0.1.4',

    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path ${env:ProgramData} 'BlockGame-install.log'
}

Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
trap {
    try {
        $logDirectory = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
            New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        }

        $details = @(
            'BlockGame installation failed.'
            "Message: $($_.Exception.Message)"
            "Position: $($_.InvocationInfo.PositionMessage)"
            "Script stack: $($_.ScriptStackTrace)"
        ) -join [Environment]::NewLine
        Set-Content -LiteralPath $LogPath -Value $details -Encoding Default
    }
    catch {
        # Preserve the original installation failure even if logging fails.
    }

    exit 1
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Copy-PublishFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory
    )

    foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -File) {
        Copy-Item -LiteralPath $file.FullName -Destination $DestinationDirectory -Force
    }
}

function Assert-FilesMatch {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Actual
    )

    if (-not (Test-Path -LiteralPath $Actual)) {
        throw "Installed file is missing: $Actual"
    }

    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed file verification failed: $Actual"
    }
}

function Register-SystemScheduledTask {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TaskName,

        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string]$ActionArguments
    )

    if ($TaskName.Contains('"') -or $Executable.Contains('"') -or $ActionArguments.Contains('"')) {
        throw "Scheduled task arguments contain an unsupported quote: $TaskName"
    }

    # Windows PowerShell 5.1 does not reliably preserve the nested quotes in
    # schtasks.exe /TR when arguments are passed with the call operator. Pass a
    # single, explicitly escaped command line so Program Files remains one path.
    $argumentLine = '/Create /TN "{0}" /TR "\"{1}\" {2}" /SC MINUTE /MO 1 /RU SYSTEM /RL HIGHEST /F' -f `
        $TaskName, $Executable, $ActionArguments
    $outputPath = Join-Path ${env:TEMP} ("BlockGame-schtasks-{0}.out" -f [Guid]::NewGuid().ToString('N'))
    $errorPath = Join-Path ${env:TEMP} ("BlockGame-schtasks-{0}.err" -f [Guid]::NewGuid().ToString('N'))

    try {
        $process = Start-Process `
            -FilePath $scheduledTaskTool `
            -ArgumentList $argumentLine `
            -Wait `
            -PassThru `
            -NoNewWindow `
            -RedirectStandardOutput $outputPath `
            -RedirectStandardError $errorPath
        $output = @(
            Get-Content -LiteralPath $outputPath -Raw -ErrorAction SilentlyContinue
            Get-Content -LiteralPath $errorPath -Raw -ErrorAction SilentlyContinue
        ) -join [Environment]::NewLine
        if ($process.ExitCode -ne 0) {
            throw "Failed to register scheduled task '$TaskName' (exit code $($process.ExitCode)): $($output.Trim())"
        }
    }
    finally {
        Remove-Item -LiteralPath $outputPath, $errorPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-IsAdministrator)) {
    throw 'Run install.ps1 from an elevated PowerShell window.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "Invalid version: $Version"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $repoRoot 'artifacts\publish'
}
$PublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path

$guardSource = Join-Path $PublishRoot 'guard'
$appSource = Join-Path $PublishRoot 'app'
$dropBridgeSource = Join-Path $PublishRoot 'dropbridge'
$uninstallerSource = Join-Path $PublishRoot 'uninstall'
$guardBinary = Join-Path $guardSource 'BlockGame.Guard.exe'
$appBinary = Join-Path $appSource 'BlockGame.App.exe'
$dropBridgeBinary = Join-Path $dropBridgeSource 'BlockGame.DropBridge.exe'
$uninstallerBinary = Join-Path $uninstallerSource 'BlockGame.Uninstall.exe'
$guardCore = Join-Path $guardSource 'BlockGame.Core.dll'
$appCore = Join-Path $appSource 'BlockGame.Core.dll'
$uninstallerCore = Join-Path $uninstallerSource 'BlockGame.Core.dll'
if (-not (Test-Path -LiteralPath $guardBinary) -or
    -not (Test-Path -LiteralPath $appBinary) -or
    -not (Test-Path -LiteralPath $dropBridgeBinary) -or
    -not (Test-Path -LiteralPath $uninstallerBinary) -or
    -not (Test-Path -LiteralPath $guardCore) -or
    -not (Test-Path -LiteralPath $appCore) -or
    -not (Test-Path -LiteralPath $uninstallerCore)) {
    throw "Publish files not found. Run scripts\build.ps1 first: $PublishRoot"
}

$guardCoreHash = (Get-FileHash -LiteralPath $guardCore -Algorithm SHA256).Hash
$appCoreHash = (Get-FileHash -LiteralPath $appCore -Algorithm SHA256).Hash
$uninstallerCoreHash = (Get-FileHash -LiteralPath $uninstallerCore -Algorithm SHA256).Hash
if (-not [string]::Equals($guardCoreHash, $appCoreHash, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($appCoreHash, $uninstallerCoreHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'App, guard, and uninstaller outputs contain different BlockGame.Core.dll files. Rebuild before installing.'
}

$installDir = Join-Path ${env:ProgramFiles} 'BlockGame'
$dataDir = Join-Path ${env:ProgramData} 'BlockGame'
$uninstallScript = Join-Path $dataDir 'uninstall-installed.ps1'
$maintenanceStopFile = Join-Path $dataDir 'maintenance-stop.request'
$serviceName = 'BlockGameGuard'
$serviceWatchdogTaskName = 'BlockGameGuardWatchdog'
$serviceRecoveryTaskName = 'BlockGameGuardRecovery'
$scheduledTaskTool = Join-Path $env:SystemRoot 'System32\schtasks.exe'

# The WPF control panel loads BlockGame.Core.dll from the installation directory.
# Stop it before copying so Windows cannot leave the new app beside an old loaded core assembly.
Get-Process -Name 'BlockGame.App' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction Stop
Get-Process -Name 'BlockGame.DropBridge' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction Stop
Get-Process -Name 'BlockGame.Uninstall' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction Stop

if (Test-Path -LiteralPath $scheduledTaskTool) {
    foreach ($taskName in @($serviceWatchdogTaskName, $serviceRecoveryTaskName)) {
        # Missing tasks are expected on a first install. Start-Process keeps
        # schtasks stderr from becoming a terminating PowerShell error.
        Start-Process `
            -FilePath $scheduledTaskTool `
            -ArgumentList @('/End', '/TN', $taskName) `
            -WindowStyle Hidden `
            -Wait `
            -ErrorAction SilentlyContinue
        Start-Process `
            -FilePath $scheduledTaskTool `
            -ArgumentList @('/Delete', '/TN', $taskName, '/F') `
            -WindowStyle Hidden `
            -Wait `
            -ErrorAction SilentlyContinue
    }
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -LiteralPath $maintenanceStopFile -Value ([Guid]::NewGuid().ToString('N')) -Encoding ASCII
    try {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item -LiteralPath $maintenanceStopFile -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force -Path $installDir, $dataDir | Out-Null
Copy-PublishFiles -SourceDirectory $guardSource -DestinationDirectory $installDir
Copy-PublishFiles -SourceDirectory $appSource -DestinationDirectory $installDir
Copy-PublishFiles -SourceDirectory $dropBridgeSource -DestinationDirectory $installDir
Copy-PublishFiles -SourceDirectory $uninstallerSource -DestinationDirectory $installDir

# All executables load this shared assembly from the installation directory.
# Copy it explicitly last and verify it so a mixed-version install cannot appear successful.
Copy-Item -LiteralPath $appCore -Destination (Join-Path $installDir 'BlockGame.Core.dll') -Force
Assert-FilesMatch -Expected $appBinary -Actual (Join-Path $installDir 'BlockGame.App.exe')
Assert-FilesMatch -Expected $dropBridgeBinary -Actual (Join-Path $installDir 'BlockGame.DropBridge.exe')
Assert-FilesMatch -Expected $guardBinary -Actual (Join-Path $installDir 'BlockGame.Guard.exe')
Assert-FilesMatch -Expected $uninstallerBinary -Actual (Join-Path $installDir 'BlockGame.Uninstall.exe')
Assert-FilesMatch -Expected $appCore -Actual (Join-Path $installDir 'BlockGame.Core.dll')

Copy-Item -LiteralPath (Join-Path $PublishRoot 'uninstall-installed.ps1') -Destination $uninstallScript -Force

# Keep the data directory private to SYSTEM and Administrators. The elevated UI and service can both access it.
$dataAcl = Get-Acl -LiteralPath $dataDir
$dataAcl.SetAccessRuleProtection($true, $false)
$dataAcl.Access | ForEach-Object { [void]$dataAcl.RemoveAccessRule($_) }
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
$propagation = [Security.AccessControl.PropagationFlags]::None
$allow = [Security.AccessControl.AccessControlType]::Allow
$dataAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($systemSid, 'FullControl', $inheritance, $propagation, $allow))
$dataAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($administratorsSid, 'FullControl', $inheritance, $propagation, $allow))
Set-Acl -LiteralPath $dataDir -AclObject $dataAcl

$guardBinary = Join-Path $installDir 'BlockGame.Guard.exe'
$binaryPath = '"{0}" --service' -f $guardBinary
New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'BlockGame Guard Service' -Description 'BlockGame process guard' -StartupType Automatic | Out-Null
& sc.exe failure $serviceName actions= restart/500/restart/1000/restart/3000 reset= 86400 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Register-SystemScheduledTask `
    -TaskName $serviceWatchdogTaskName `
    -Executable $guardBinary `
    -ActionArguments '--watch-service'

Register-SystemScheduledTask `
    -TaskName $serviceRecoveryTaskName `
    -Executable $guardBinary `
    -ActionArguments '--ensure-service-running'

& $scheduledTaskTool /Run /TN $serviceWatchdogTaskName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to start the continuous guard service watchdog task.'
}

$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BlockGame'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'BlockGame Self-Control Assistant' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $Version -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'BlockGame' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value (Join-Path $installDir 'BlockGame.App.exe') -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "`"$(Join-Path $installDir 'BlockGame.Uninstall.exe')`"" -PropertyType String -Force | Out-Null

$startMenu = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs\BlockGame.lnk'
$programsDirectory = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
$desktopShortcut = Join-Path $desktopDirectory 'BlockGame.lnk'
$uninstallShortcutName = (-join @([char]0x5378, [char]0x8F7D, ' BlockGame.lnk'))
$uninstallStartMenu = Join-Path $programsDirectory $uninstallShortcutName
$shell = New-Object -ComObject WScript.Shell

# Remove only legacy BlockGame shortcuts that target this uninstaller. This also
# cleans a filename created by older Windows PowerShell encoding behavior.
Get-ChildItem -LiteralPath $programsDirectory -Filter '*BlockGame.lnk' -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -ne $uninstallStartMenu -and
        $shell.CreateShortcut($_.FullName).TargetPath -eq (Join-Path $installDir 'BlockGame.Uninstall.exe')
    } |
    Remove-Item -Force

$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = Join-Path $installDir 'BlockGame.App.exe'
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'BlockGame Self-Control Assistant'
$shortcut.Save()
$desktopLink = $shell.CreateShortcut($desktopShortcut)
$desktopLink.TargetPath = Join-Path $installDir 'BlockGame.App.exe'
$desktopLink.WorkingDirectory = $installDir
$desktopLink.Description = 'BlockGame Self-Control Assistant'
$desktopLink.Save()
$uninstallShortcut = $shell.CreateShortcut($uninstallStartMenu)
$uninstallShortcut.TargetPath = Join-Path $installDir 'BlockGame.Uninstall.exe'
$uninstallShortcut.WorkingDirectory = $installDir
$uninstallShortcut.Description = 'Uninstall BlockGame (management password required)'
$uninstallShortcut.Save()

Start-Service -Name $serviceName
Write-Host "BlockGame installed."
Write-Host "Management UI: $installDir\BlockGame.App.exe"
Write-Host "Set a password, add rules, and enable the lock on first launch."
