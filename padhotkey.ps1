param([Parameter(ValueFromRemainingArguments=$true)][string[]]$CliArgs)
$exe = Join-Path $PSScriptRoot 'local\portable\PadHotkey.exe'
if (!(Test-Path -LiteralPath $exe)) { $exe = Join-Path $PSScriptRoot 'local\publish\PadHotkey.exe' }
if (!(Test-Path -LiteralPath $exe)) { throw 'Run .\build.ps1 -Portable first.' }
& $exe @CliArgs
exit $LASTEXITCODE
