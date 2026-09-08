# Glissando

One keyboard and mouse for two Windows PCs that cannot see each other on any
network. Slide the cursor off the edge of one screen and it appears on the
other, keystrokes follow, and clipboard text comes along. The link is
Bluetooth Low Energy, so it works across corporate networks, domains and
firewalls, and it needs no administrator rights on either machine.

Status: **early prototype** (v0.3). Built and exercised on the author's own
two machines; expect rough edges.

## How it differs from Mouse without Borders, Synergy, Input Leap

Those all need an IP route between the PCs. Glissando needs the PCs to be
within Bluetooth range of each other, nothing else. The trade-off is latency:
measured round trip over BLE is about 13 ms median, 18 ms at the 95th
percentile, which feels like a wireless mouse. Classic Bluetooth (RFCOMM) was
tried first and rejected: Windows keeps that link in a 50 ms sniff cycle, which
makes the mouse rubbery.

## Setup

1. Pair the two PCs once in Windows Settings (Bluetooth & devices, Add device,
   with the Bluetooth page open on both so they are discoverable). The setup
   dialog has a button that opens that page.
2. Run `Glissando.exe` on both PCs. The first run opens the setup dialog:
   generate a passphrase, type the same one on the other PC, and drag the
   blue screens to where the other PC physically stands. Until the PCs have
   linked once, the other PC is drawn as a single box; after that its real
   monitors appear and you can fine-tune.
3. The tray icon turns green when the two PCs have found each other and
   authenticated. Push the cursor against the shared edge to cross over, and
   against the same edge on the other PC to come back. **Ctrl+Alt+Esc** on
   the PC whose keyboard you are typing on always returns control locally.
   **Ctrl+Alt+L** (or the tray menu) locks the cursor to the PC you are on,
   for games, presentations or anything that lives against the shared edge;
   press it again to unlock. **Ctrl+Alt+J** jumps to the middle of the other
   PC's first screen without dragging to an edge, and back again. The tray
   menu shows the measured round trip to the other PC, and *Copy
   diagnostics* puts everything useful for a bug report on the clipboard.

Locking the Windows session releases control on both sides; the tray
status says when the other PC is locked, since it can only be unlocked with
its own keyboard. *Save power when idle* (off by default) slows the
keepalive after five minutes without input so the Bluetooth link can rest;
the first movement after such a pause takes a little longer to arrive.

There is no host and client to choose: both PCs run the same thing, and
whichever PC's own mouse crosses the edge takes control of the other. Untick
"may control the other PC" on a machine that should only ever be controlled.

The placement only needs to be set on one PC; the other adopts it (mirrored)
when they connect. Whichever PC's placement was edited most recently wins.

Which PC advertises over Bluetooth is decided automatically: both advertise
if they can, and the one with the lower Bluetooth address connects. If a PC's
adapter cannot advertise, it connects. The settings dialog can pin the role
should that ever misbehave.

Settings live in `%APPDATA%\Glissando\settings.json`; the log in
`%LOCALAPPDATA%\Glissando\glissando.log`. Command-line overrides for a
single run:

```
Glissando.exe --setup                 open the settings dialog first
Glissando.exe --ble connect --console pin the Bluetooth role, show the log
```

## What works and what does not

* Mouse movement, buttons, wheel; keyboard including Win and Alt combinations.
  Keys are sent as scan codes, so both PCs should use the same keyboard layout.
* Clipboard text is copied across each time the cursor crosses over.
* Multi-monitor: both PCs' monitors live in one shared coordinate space. The
  other PC's screens can stand beside, above, below, or in an empty corner of
  this PC's desktop; every monitor edge that borders one of its screens, with
  none of your own in between, is a crossing. The settings dialog draws all
  monitors numbered as in Windows Display settings on each PC, snaps the
  other PC flush against your edges, and marks every crossing in green. The
  monitor layouts travel in the handshake, so the placement only has to be
  drawn on one PC.
* Not possible without administrator rights on the controlled PC: typing into
  UAC prompts, elevated windows, or the lock screen. Unlock it with its own
  keyboard.
* Optional: hide this PC's cursor while it controls the other PC. Windows
  offers no per-app way to hide the cursor over other programs' windows
  without administrator rights, so this swaps the whole system cursor set
  for blank cursors and reloads your cursor scheme when control returns
  (also on exit or crash). Off by default; turn it on in the settings dialog.
* Not yet: files on the clipboard, absolute-position devices (tablets, RDP)
  as the controlling mouse.

## Security

Every frame is AES-256-GCM encrypted with a key derived from the passphrase
(PBKDF2, 200 000 rounds). Frame counters start from the clock and must
increase, so replayed frames are dropped, and each side proves it holds the
key with a nonce handshake before any input is accepted. The passphrase is
stored in plain text in `settings.json`; treat that file accordingly.

## How it works

```
 controlling PC                                     controlled PC
 ──────────────                                     ─────────────
 WH_MOUSE_LL / WH_KEYBOARD_LL ─ swallow while remote
 Raw Input (WM_INPUT) ───────── relative mouse deltas
        │  frames, AES-GCM                                  SendInput ◀─┐
        ▼                                                               │
 BLE GATT: one Write-Without-Response characteristic (central → peripheral)
           one Notify characteristic (peripheral → central)       ──────┘
```

Both PCs run both halves. Mouse moves are coalesced in the send queue so a
stalled link never builds a backlog; a 1 Hz ping keeps Windows from
lengthening the BLE connection interval; the peer is dropped after 4 s of
silence and all keys are released. Injected input is never captured again,
so the two PCs cannot feed each other in a loop.

## Build

Needs the .NET 8 SDK. `build.ps1` publishes a self-contained single-file exe
to `%USERPROFILE%\Program\Glissando` and `dist\`:

```powershell
.\build.ps1
```

The first start of a freshly built exe takes 10 to 30 seconds while Windows
Defender scans the new 65 MB file; every start after that takes about a
second. Nothing is extracted to disk any more.

The crossing, snapping, protocol and crypto logic has unit tests:

```powershell
dotnet test tests\Glissando.Tests
```

To preview the placement dialog with a monitor layout you do not have, set
`GLISSANDO_FAKE_MONITORS` to `x,y,w,h;x,y,w,h;...` before starting with
`--setup`.

`tools\bttest` is the throwaway latency probe used to choose the transport
(RFCOMM vs BLE, various connection parameters); its logs are kept there.

## Name

A glissando slides from one note to the next without a break. Sibling tools
in the same family: DalSegno, Sostenuto, Attacca, Segue.
