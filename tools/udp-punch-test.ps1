# Two-PC test: can two non-admin Glissando instances reach each other over UDP through Windows Firewall
# with NO firewall rule? The firewall blocks unsolicited inbound, but lets in UDP that answers a flow this PC
# opened itself. So if BOTH sides start sending to the other's IP:port, each side's firewall sees only
# "replies" and lets them through (the same trick VoIP and games use). Run this on both PCs at the same time:
#
#   powershell -ExecutionPolicy Bypass -File udp-punch-test.ps1 -Peer <other PC's IP>
#
# Both must use the same -Port (default 47345). Success = both windows print "RECV from ..." lines.
# Also measures rough one-way throughput with a 5 s burst of 1200-byte packets at the end.
param(
    [Parameter(Mandatory)] [string]$Peer,
    [int]$Port = 47345,
    [int]$Seconds = 25
)
$ErrorActionPreference = "Stop"
$me = $env:COMPUTERNAME
$udp = New-Object System.Net.Sockets.UdpClient($Port)
$udp.Client.ReceiveTimeout = 200
$remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($Peer)), $Port
Write-Host "[$me] listening on UDP $Port, sending to $Peer`:$Port for $Seconds s. Start the same on the other PC now."

$enc = [System.Text.Encoding]::ASCII
$sent = 0; $recv = 0; $firstRecv = $null; $burstBytes = 0; $burstPkts = 0; $burstStart = $null
$t0 = Get-Date
$lastSend = [DateTime]::MinValue
$any = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    $now = Get-Date
    if (($now - $lastSend).TotalMilliseconds -ge 250) {
        $sent++
        $msg = $enc.GetBytes("hello from $me #$sent $($now.ToString('HH:mm:ss.fff'))")
        [void]$udp.Send($msg, $msg.Length, $remote)
        $lastSend = $now
    }
    try {
        $data = $udp.Receive([ref]$any)
        if ($data.Length -ge 1200) {
            if ($null -eq $burstStart) { $burstStart = Get-Date }
            $burstBytes += $data.Length; $burstPkts++
        } else {
            $recv++
            if ($null -eq $firstRecv) { $firstRecv = Get-Date; Write-Host "[$me] *** FIRST PACKET THROUGH after $([int](($firstRecv - $t0).TotalMilliseconds)) ms ***" -ForegroundColor Green }
            if ($recv -le 5 -or $recv % 20 -eq 0) { Write-Host "[$me] RECV from $($any.Address): $($enc.GetString($data))" }
        }
    } catch [System.Net.Sockets.SocketException] { }   # timeout: nothing received this round
}

# Throughput burst: 5 s of 1200-byte packets as fast as the socket takes them (other side counts them).
Write-Host "[$me] burst: sending 1200-byte packets for 5 s ..."
$payload = New-Object byte[] 1200
$bt = Get-Date; $bsent = 0
while (((Get-Date) - $bt).TotalSeconds -lt 5) { [void]$udp.Send($payload, $payload.Length, $remote); $bsent++; if ($bsent % 50 -eq 0) { Start-Sleep -Milliseconds 1 } }
# keep receiving the other side's burst for a few seconds more
$bt2 = Get-Date
while (((Get-Date) - $bt2).TotalSeconds -lt 7) {
    try { $data = $udp.Receive([ref]$any); if ($data.Length -ge 1200) { if ($null -eq $burstStart) { $burstStart = Get-Date }; $burstBytes += $data.Length; $burstPkts++ } } catch [System.Net.Sockets.SocketException] { }
}
$udp.Close()

Write-Host ""
Write-Host "[$me] RESULT: sent $sent hellos, received $recv hellos from $Peer" -ForegroundColor Cyan
if ($recv -eq 0) { Write-Host "[$me] nothing came through - firewall (or NAT/routing) blocks this direction, or the other side was not running." -ForegroundColor Yellow }
Write-Host "[$me] burst: sent $bsent x 1200 B; received $burstPkts x 1200 B = $([int]($burstBytes/1024)) kB" -ForegroundColor Cyan
if ($burstPkts -gt 0 -and $burstStart) { $dur = ((Get-Date) - $burstStart).TotalSeconds; if ($dur -gt 7) { $dur = 5 }; Write-Host "[$me] approx inbound throughput: $([int]($burstBytes/1024/[Math]::Max(1,$dur))) kB/s (loss: $([Math]::Max(0, 100 - [int](100*$burstPkts/[Math]::Max(1,$bsent))))% vs peer's likely send count)" -ForegroundColor Cyan }
