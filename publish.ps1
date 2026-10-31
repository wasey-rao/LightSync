# Builds LightSync into .\dist\LightSync.exe (single file; needs the .NET 10 Desktop Runtime).
# Pass -SelfContained to bundle the runtime too (bigger, but runs on any Windows 10/11 PC).
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "LightSync\LightSync.csproj"
$out = Join-Path $PSScriptRoot "dist"

$running = Get-Process LightSync -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" }
if ($running) { throw "LightSync is running from $out. Exit it from the tray icon first." }

dotnet publish $project -c Release -r win-x64 -o $out `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "`nBuilt $(Join-Path $out 'LightSync.exe')" -ForegroundColor Green
