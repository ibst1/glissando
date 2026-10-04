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
#
# Publish into a staging folder first, never straight into the folder the program runs from. Twice a publish
# that failed half-way -- killed under load, then refused because another process had the fresh exe open --
# left a truncated Glissando.exe there, and the next start had nothing to run (2026-10-02, 2026-10-04).
$stage = Join-Path $root "src\Glissando\bin\publish"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
& $dotnet publish $proj -c $Configuration -r $Runtime --self-contained $selfContained `
    -p:PublishSingleFile=true -p:Version=$version -p:InformationalVersion=$info -p:BuildTime="$buildTime" -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$staged = Join-Path $stage "Glissando.exe"
if (-not (Test-Path $staged)) { throw "publish produced no Glissando.exe" }
$stagedSize = (Get-Item $staged).Length

function Copy-WithRetry([string]$from, [string]$to) {
    # A scanner or a sync tool may hold the destination for a moment; try for a few seconds, and check the size.
    for ($i = 1; $i -le 12; $i++) {
        try {
            Copy-Item $from $to -Force -ErrorAction Stop
            if ((Get-Item $to).Length -eq (Get-Item $from).Length) { return }
        } catch { if ($i -eq 12) { throw } }
        Start-Sleep -Milliseconds 700
    }
    throw "could not copy $from to $to intact"
}

# A running Glissando locks its exe, but a running exe may still be RENAMED: move it aside, so the instance
# keeps running from the renamed file and the next start picks up the new one. A stump left by an earlier
# failed publish is not worth keeping as the previous version; just delete it.
New-Item -ItemType Directory -Force $target | Out-Null
$exe = Join-Path $target "Glissando.exe"
if (Test-Path $exe) {
    $prev = Join-Path $target "Glissando.prev.exe"
    if ((Get-Item $exe).Length -lt $stagedSize / 2) {
        Remove-Item $exe -Force -ErrorAction SilentlyContinue
    } else {
        Remove-Item $prev -Force -ErrorAction SilentlyContinue
        try { Move-Item $exe $prev -Force } catch { Write-Host "note: could not move the old exe aside ($_)" }
    }
}
Get-ChildItem $stage -File | ForEach-Object { Copy-WithRetry $_.FullName (Join-Path $target $_.Name) }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-WithRetry $staged (Join-Path $dist "Glissando.exe")
Write-Host "Published to $target and $dist"
