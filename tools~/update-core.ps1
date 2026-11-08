# Rebuilds Runtime/Plugins/NetSync.dll + NetSync.xml from a tagged revision of the core repository.
#   powershell -File tools~/update-core.ps1 -CorePath ..\NetSync -Ref v2.0.0-rc.3
# The revision is exported to a temporary folder, so uncommitted work in the core checkout never ends up in the package.
param(
    [string]$CorePath = "..\NetSync",
    [string]$Ref = "v2.0.0-rc.3"
)
$ErrorActionPreference = "Stop"

$plugins = Join-Path $PSScriptRoot "..\Runtime\Plugins"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("netsync-core-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp "core.zip"
    git -C $CorePath archive --format=zip -o $zip $Ref
    if ($LASTEXITCODE -ne 0) { throw "git archive failed for '$Ref' in '$CorePath'" }
    Expand-Archive -Path $zip -DestinationPath (Join-Path $temp "src")

    $project = Join-Path $temp "src\src\NetSync\NetSync.csproj"
    dotnet build $project -c Release -f netstandard2.1
    if ($LASTEXITCODE -ne 0) { throw "Core build failed" }

    $output = Join-Path $temp "src\src\NetSync\bin\Release\netstandard2.1"
    Copy-Item (Join-Path $output "NetSync.dll") $plugins -Force
    Copy-Item (Join-Path $output "NetSync.xml") $plugins -Force
    Write-Host "Runtime/Plugins updated from $Ref. Set the same version in package.json."
}
finally {
    Remove-Item -Recurse -Force $temp
}
