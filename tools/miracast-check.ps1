# Can this PC RECEIVE a Miracast projection ("Projecting to this PC")? That needs the optional "Wireless Display"
# feature, which only an administrator can add. Being able to project TO a TV proves only the SEND side.
# Run on the PC that should act as the extra screen:
#   powershell -ExecutionPolicy Bypass -File miracast-check.ps1
$me = $env:COMPUTERNAME
Write-Host "[$me] Miracast check"
Write-Host "--- SEND side (driver): ---"
netsh wlan show drivers | Select-String 'Wireless Display' | ForEach-Object { Write-Host ("  " + $_.Line.Trim()) }
Write-Host "--- RECEIVE side (Wireless Display optional feature): ---"
$apps = Get-StartApps | Where-Object { $_.Name -match '^(Connect|Anslut)$' }
$cbs = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages' -ErrorAction SilentlyContinue |
       Where-Object { $_.PSChildName -match 'WirelessDisplay' }
$folder = Test-Path "$env:SystemRoot\SystemApps\Microsoft.PPIProjection_cw5n1h2txyewy"
Write-Host "  Connect app in Start menu : $(if ($apps) { 'YES  (' + ($apps.Name -join ', ') + ')' } else { 'no' })"
Write-Host "  WirelessDisplay CBS pkgs  : $(if ($cbs) { 'YES  ' + (($cbs | ForEach-Object { '{0} state=0x{1:X}' -f $_.PSChildName, (Get-ItemProperty $_.PSPath).CurrentState }) -join '; ') } else { 'none' })"
Write-Host "  PPIProjection app folder  : $(if ($folder) { 'YES' } else { 'no' })"
$installed = [bool]$apps -or [bool]$folder -or ($cbs | Where-Object { (Get-ItemProperty $_.PSPath).CurrentState -eq 0x70 })
Write-Host ""
if ($installed) { Write-Host "[$me] RESULT: Wireless Display looks INSTALLED - this PC can be projected to (Win+K on the other PC -> Extend)." -ForegroundColor Green }
else { Write-Host "[$me] RESULT: Wireless Display is NOT installed - this PC cannot receive a projection (adding it needs an administrator)." -ForegroundColor Yellow }
