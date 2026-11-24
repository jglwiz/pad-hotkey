param([switch]$DryRun, [int]$Seconds = 0)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'local\portable\PadHotkey.exe'
if (!(Test-Path -LiteralPath $exe)) { $exe = Join-Path $PSScriptRoot 'local\publish\PadHotkey.exe' }
if (!(Test-Path -LiteralPath $exe)) { throw 'Run .\build.ps1 -Portable first.' }
if ($Seconds -lt 0 -or $Seconds -gt 86400) { throw 'Seconds must be 0..86400' }
$backend = if ($DryRun) { 'dry-run' } else { 'hid' }
$launch = @{ FilePath = $exe; ArgumentList = @('serve','--backend',$backend,'--seconds',"$Seconds"); WindowStyle = 'Hidden'; PassThru = $true }
if (!$DryRun) { $launch.Verb = 'RunAs' }
$process = Start-Process @launch
Write-Output "PadHotkey launched, PID $($process.Id). Mapping enabled; use .\padhotkey.ps1 status to verify."
