<#
.SYNOPSIS
    Stops NGenuityLeakFix, removes the Startup shortcut and the installed files.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
param(
    [switch]$KeepFiles,
    [switch]$Quiet
)

$target = Join-Path $env:LOCALAPPDATA 'NGenuityLeakFix'
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'NGenuityLeakFix.lnk'

Get-Process NGenuityLeakFix -ErrorAction SilentlyContinue | Stop-Process -Force

# Script mode runs inside powershell.exe; match it by the installed run.ps1 path.
$runPs1 = Join-Path $target 'run.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($runPs1) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
if (-not $KeepFiles) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }

if (-not $Quiet) { Write-Host 'NGenuityLeakFix removed.' }
