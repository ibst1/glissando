# Builds Glissando as a self-contained single-file exe and copies it to
# %USERPROFILE%\Program\Glissando (a location unsigned exes may run from on
# locked-down machines) and to dist\ next to this script.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Glissando\Glissando.csproj"
$dist = Join-Path $root "dist"
$target = Join-Path $env:USERPROFILE "Program\Glissando"

# Prefer a user-local SDK if the machine-wide dotnet has none.
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
& $dotnet publish $proj -c $Configuration -r $Runtime --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $target
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $target "Glissando.exe") (Join-Path $dist "Glissando.exe") -Force
Write-Host "Published to $target and $dist"
