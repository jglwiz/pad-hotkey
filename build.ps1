param([switch]$Publish, [switch]$Portable)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\PadHotkey\PadHotkey.csproj'
if ($Portable) {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $PSScriptRoot 'local\portable')
} elseif ($Publish) {
    & dotnet publish $project -c Release --no-self-contained -o (Join-Path $PSScriptRoot 'local\publish')
} else {
    & dotnet build $project -c Release
}
if ($LASTEXITCODE -ne 0) { throw 'PadHotkey build failed' }
