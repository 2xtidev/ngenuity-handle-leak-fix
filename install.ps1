<#
.SYNOPSIS
    Installs NGenuityLeakFix for the current user and starts it.
    No administrator rights needed.

    Copies the files to %LOCALAPPDATA%\NGenuityLeakFix and adds a shortcut to
    your Startup folder, so it starts every time you log on.

.PARAMETER Mode
    exe    - runs NGenuityLeakFix.exe (default)
    script - runs run.ps1, which compiles the C# source at start-up; no binary

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Mode script
#>
param(
    [ValidateSet('exe', 'script')]
    [string]$Mode = 'exe'
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'NGenuityLeakFix'
$startup = [Environment]::GetFolderPath('Startup')
$shortcut = Join-Path $startup 'NGenuityLeakFix.lnk'

function Find-File([string[]]$candidates) {
    foreach ($c in $candidates) {
        $p = Join-Path $here $c
        if (Test-Path $p) { return $p }
    }
    return $null
}

# Stop a running copy so its files can be replaced.
& (Join-Path $here 'uninstall.ps1') -KeepFiles -Quiet

New-Item -ItemType Directory -Force $target | Out-Null

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.WorkingDirectory = $target
$lnk.Description = 'Closes the process handles NGenuity2Helper.exe leaks'

if ($Mode -eq 'exe') {
    $exe = Find-File @('NGenuityLeakFix.exe', 'dist\NGenuityLeakFix.exe', 'bin\NGenuityLeakFix.exe')
    if (-not $exe) { throw 'NGenuityLeakFix.exe not found next to install.ps1. Download the release zip, or use -Mode script.' }
    Copy-Item $exe $target -Force
    $lnk.TargetPath = Join-Path $target 'NGenuityLeakFix.exe'
    $lnk.Arguments = ''
}
else {
    $cs = Find-File @('src\NGenuityLeakFix.cs', 'NGenuityLeakFix.cs')
    if (-not $cs) { throw 'src\NGenuityLeakFix.cs not found next to install.ps1.' }
    Copy-Item (Join-Path $here 'run.ps1') $target -Force
    Copy-Item $cs $target -Force
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lnk.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $target 'run.ps1') + '" -Tray'
    $lnk.WindowStyle = 7
}
$lnk.Save()

if ($lnk.Arguments) {
    Start-Process -FilePath $lnk.TargetPath -ArgumentList $lnk.Arguments -WorkingDirectory $target -WindowStyle Hidden
}
else {
    Start-Process -FilePath $lnk.TargetPath -WorkingDirectory $target
}

Write-Host "Installed ($Mode mode) to $target"
Write-Host "Starts at logon via $shortcut"
Write-Host "Look for the round NGenuityLeakFix icon in the notification area; double-click it for the status window."
Write-Host "Log: $target\NGenuityLeakFix.log"
