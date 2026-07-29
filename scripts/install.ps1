[CmdletBinding()]
param(
    [string]$PublishRoot = ''
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw 'Run install.ps1 from an elevated PowerShell window.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $repoRoot 'artifacts\publish'
}
$PublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path

$guardSource = Join-Path $PublishRoot 'guard'
$appSource = Join-Path $PublishRoot 'app'
$guardBinary = Join-Path $guardSource 'BlockGame.Guard.exe'
$appBinary = Join-Path $appSource 'BlockGame.App.exe'
if (-not (Test-Path -LiteralPath $guardBinary) -or -not (Test-Path -LiteralPath $appBinary)) {
    throw "Publish files not found. Run scripts\build.ps1 first: $PublishRoot"
}

$installDir = Join-Path ${env:ProgramFiles} 'BlockGame'
$dataDir = Join-Path ${env:ProgramData} 'BlockGame'
$uninstallScript = Join-Path $dataDir 'uninstall-installed.ps1'
$maintenanceStopFile = Join-Path $dataDir 'maintenance-stop.request'
$serviceName = 'BlockGameGuard'

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
Copy-Item -Path (Join-Path $guardSource '*') -Destination $installDir -Recurse -Force
Copy-Item -Path (Join-Path $appSource '*') -Destination $installDir -Recurse -Force
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

$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BlockGame'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'BlockGame Self-Control Assistant' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.1.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'BlockGame' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`"" -PropertyType String -Force | Out-Null

$startMenu = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs\BlockGame.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = Join-Path $installDir 'BlockGame.App.exe'
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'BlockGame Self-Control Assistant'
$shortcut.Save()

Start-Service -Name $serviceName
Write-Host "BlockGame installed."
Write-Host "Management UI: $installDir\BlockGame.App.exe"
Write-Host "Set a password, add rules, and enable the lock on first launch."
