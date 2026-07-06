# Rebuilds the NetSync core (expected as a sibling checkout) and refreshes the
# precompiled DLL this package ships. Run after pulling core changes:
#   powershell -File tools/update-core.ps1 [-CorePath ..\NetSync]
param(
    [string]$CorePath = (Join-Path $PSScriptRoot "..\..\NetSync")
)

$ErrorActionPreference = "Stop"
$coreProject = Join-Path $CorePath "src\NetSync\NetSync.csproj"
if (-not (Test-Path $coreProject)) {
    throw "NetSync core not found at '$CorePath'. Pass -CorePath pointing at the core repo."
}

dotnet build $coreProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Core build failed" }

$binDir = Join-Path $CorePath "src\NetSync\bin\Release\netstandard2.1"
$pluginsDir = Join-Path $PSScriptRoot "..\Runtime\Plugins"
Copy-Item (Join-Path $binDir "NetSync.dll") $pluginsDir -Force
Copy-Item (Join-Path $binDir "NetSync.xml") $pluginsDir -Force
Write-Host "Updated Runtime/Plugins/NetSync.dll from $binDir"
