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

# Every build gets its own version: 0.3.<commit count>, plus the short hash (and "dirty" when the
# tree has uncommitted changes) and the build time. Shown in the tray, the dialog title, the
# handshake and the exe's file properties, so two PCs can be compared at a glance.
$major = "0.3"
Push-Location $root
try {
    $count = (& git rev-list --count HEAD 2>$null); if (-not $count) { $count = "0" }
    $hash = (& git rev-parse --short HEAD 2>$null); if (-not $hash) { $hash = "nogit" }
    $dirty = if (& git status --porcelain 2>$null) { ".dirty" } else { "" }
} finally { Pop-Location }
$version = "$major.$count"
$info = "$version+$hash$dirty"
$buildTime = Get-Date -Format "yyyy-MM-dd HH:mm"
Write-Host "Building Glissando $info ($buildTime)"

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
# No IncludeNativeLibrariesForSelfExtract: we ship no native libraries of our own, and with it the
# first start of every new build spent ~30 s extracting the runtime to a cache.
& $dotnet publish $proj -c $Configuration -r $Runtime --self-contained $selfContained `
    -p:PublishSingleFile=true -p:Version=$version -p:InformationalVersion=$info -p:BuildTime="$buildTime" -o $target
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $target "Glissando.exe") (Join-Path $dist "Glissando.exe") -Force
Write-Host "Published to $target and $dist"
