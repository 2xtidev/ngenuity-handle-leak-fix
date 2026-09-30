<#
.SYNOPSIS
    Script variant of NGenuityLeakFix: compiles src\NGenuityLeakFix.cs in
    memory with Add-Type and runs it. No binary involved - read the .cs file
    and this script, and that is everything that runs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\run.ps1 -DryRun -Once
    Shows what would be closed, closes nothing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\run.ps1 -Detailed
    Runs in the foreground and prints every pass.
#>
param(
    [int]$Interval = 5,
    [int]$MinAge = 30,
    [int]$Threshold = 500,
    [switch]$DryRun,
    [switch]$Once,
    [switch]$Detailed,
    [string]$Log
)

$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitProcess) {
    throw 'Run this from 64-bit Windows PowerShell.'
}

$source = Join-Path $PSScriptRoot 'src\NGenuityLeakFix.cs'
if (-not (Test-Path $source)) { $source = Join-Path $PSScriptRoot 'NGenuityLeakFix.cs' }
if (-not ('NGenuityLeakFix.Program' -as [type])) {
    Add-Type -Path $source
}

$argList = @('--interval', $Interval, '--min-age', $MinAge, '--threshold', $Threshold, '--console')
if ($DryRun)   { $argList += '--dry-run' }
if ($Once)     { $argList += '--once' }
if ($Detailed) { $argList += '--verbose' }
if ($Log)      { $argList += @('--log', $Log) }

$options = [NGenuityLeakFix.Options]::Parse([string[]]($argList | ForEach-Object { "$_" }))
exit [NGenuityLeakFix.Program]::Run($options)
