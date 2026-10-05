# Glissando

One keyboard and mouse for two Windows PCs that cannot see each other on any
network. Slide the cursor off the edge of one screen and it appears on the
other, keystrokes follow, and clipboard text comes along. The link is
Bluetooth Low Energy, so it works across corporate networks, domains and
firewalls, and it needs no administrator rights on either machine.

Status: **working prototype** (v0.3). Mouse, keyboard (including held
modifiers like Shift+arrow and Ctrl+C/V) and clipboard work in both
directions between the author's two machines over Bluetooth LE. Link
quality between the specific adapters sets the latency floor.

## How it differs from Mouse without Borders, Synergy, Input Leap

Those all need an IP route between the PCs. Glissando needs the PCs to be
within Bluetooth range of each other, nothing else. The trade-off is latency:
measured round trip over BLE is about 13 ms median, 18 ms at the 95th
percentile, which feels like a wireless mouse. Classic Bluetooth (RFCOMM) was
tried first and rejected: Windows keeps that link in a 50 ms sniff cycle, which
makes the mouse rubbery.

## Setup

1. Turn Bluetooth on on both PCs. Pairing them in Windows is not needed: the
   link is a plain GATT connection, and the two PCs run unpaired. The setup
   dialog has a button that opens the Bluetooth page.
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
   PC's first screen without dragging to an edge, and back again. All three
   want the *left* Ctrl, so a remapper that turns CapsLock into right Ctrl
   (Sostenuto does) cannot trip them. The tray
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
To take a machine back while it is being controlled, just move its own
mouse or press a key on it.

The mouse and the keyboard need not belong to the same PC. With a mouse
paired to one PC and the keyboard you type on attached to the other, turn on
*This PC's keyboard follows the mouse used last* on the keyboard's PC. Once
the other PC's mouse has visited and gone home, keys typed there go to the
other PC; they type locally while that cursor is over on the keyboard's PC,
and from the moment the keyboard's PC's own mouse moves, until the other
mouse visits again. A key no longer takes the machine back while the other
cursor is here (the PC's own mouse still does). **Ctrl+Alt+L** keeps the
keyboard local until pressed again.

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

* Mouse movement, buttons and wheel; keyboard including Win and Alt combinations.
  Keys are sent as scan codes, so both PCs should use the same keyboard layout.
  The wheel is read from Raw Input, so a mouse scrolls the other PC; a **precision
  touchpad's two-finger scroll does not cross**, because Windows delivers it as
  digitizer input that neither the low-level mouse hook nor mouse Raw Input sees.
  Scroll the other PC with a mouse wheel or the keyboard (PageUp/PageDown, arrows).
* The clipboard is copied across each time the cursor crosses over: text, or
  else a picture (a Win+Shift+S screenshot). Over the network path a picture
  of any size goes in a moment; over Bluetooth alone it is sent as PNG up to
  300 kB, as JPEG above that, and not at all if still bigger, since it would
  take longer than anyone waits.
* A **Bluetooth mouse or keyboard on the controlling PC shares this one Bluetooth
  radio with Glissando.** While you move such a mouse it floods the radio with its
  own reports and the link to the other PC stutters, felt as lag in the direction
  you are driving and only while the mouse moves. Drive from a wired mouse, a
  2.4 GHz-dongle mouse, or the trackpad; a Bluetooth mouse is fine on the PC being
  controlled. Glissando cannot schedule around a second device on the same radio.
* Multi-monitor: both PCs' monitors live in one shared coordinate space. The
  other PC's screens can stand beside, above, below, or in an empty corner of
  this PC's desktop; every monitor edge that borders one of its screens, with
  none of your own in between, is a crossing. The settings dialog draws all
  monitors numbered as in Windows Display settings on each PC, snaps the
  other PC flush against your edges, and marks every crossing in green. The
  monitor layouts travel in the handshake, so the placement only has to be
  drawn on one PC. Once a minute each PC re-checks its own monitors and
  re-sends its layout, so a display change that Windows reported late (a
  dock waking up) or a lost handshake packet cannot leave the other PC with
  a stale arrangement for long.
* Not possible without administrator rights on the controlled PC: typing into
  UAC prompts, elevated windows, or the lock screen. Unlock it with its own
  keyboard.
* Optional: hide this PC's cursor while it controls the other PC. Windows
  offers no per-app way to hide the cursor over other programs' windows
  without administrator rights, so this swaps the whole system cursor set
  for blank cursors and reloads your cursor scheme when control returns
  (also on exit or crash). Off by default; turn it on in the settings dialog.
* Not yet: files on the clipboard; absolute-position devices (tablets, RDP) as the
  controlling mouse; forwarding a precision touchpad's two-finger scroll (would
  need raw touchpad-HID parsing and two-finger gesture detection on the
  controlling side).

## Mirroring a window

Sometimes you do not want to move over to the other PC, only to see and use one
window that lives there -- a build running, a chat, a document. Click the window
you want, then pick **Mirror the active window** from the tray: it opens on this
PC as an ordinary window. Click, type and scroll in it and the input goes
back to the source window; the source PC brings that window to the front and
replays the input there, so nothing needs a keyboard at that end. The mirror
behaves like the window itself: it shows the picture at 1:1, resizing it
resizes the source window (when you let go of the border), and closing it
stops the mirror.

Several windows can be mirrored at once: click another window and pick the
tray item again. Each gets its own window on the other PC, titled after its
source, and the link's capacity is shared out between them. The tray menu
lists a **Stop mirroring "..."** item for each mirrored window, and **Stop all
mirrors** when there are several. A mirror also stops by itself when its
source window closes.

While it is mirrored, the window is invisible on the PC it lives on, as if it
had moved over (an option, on by default). It keeps its taskbar button and
Alt+Tab entry, and it shows again when the mirror stops or the moment someone
clicks or types on that PC, so an invisible window can never sit in front of
a person swallowing their clicks. (A modifier alone, such as the Ctrl and Alt
of Ctrl+Alt+J, does not count as typing.) Hovering over the mirror moves the
source PC's cursor over the hidden window so that hover effects show; while
the window is hidden that cursor is hidden too, and it comes back as soon as
that PC's own mouse moves or the other PC's mouse crosses over to it. While the
other PC's mouse is over driving the source PC, the hidden window lets clicks
through to whatever lies behind it, so it cannot catch them unseen; if it
held the focus, the window behind it gets the focus instead, so typing does
not vanish into it either. The trick is a layered window at 1/255
opacity: the only way of hiding a window that still lets it take clicks and
keys and become the foreground window, which the input replay needs (moving
it off screen or DWM-cloaking it would break that). If Glissando is ever
killed with a window hidden, the next start makes it visible again.

The picture is sent as small JPEG tiles, only the ones that changed, so a still
window costs almost nothing and typing shows at once. Each frame is capped in
bytes -- tight when this PC is the BLE peripheral, whose direction is the slower
one -- so a big repaint (a scroll, a browser redraw) is spread over a few frames
rather than dumped in one burst that would stall the link and the input with it;
the window then takes about a second to finish redrawing a large change instead
of freezing. Menus, drop-downs and dialogs show too, because while the source
window is in front its picture is taken from the screen rather than from the
window alone. The tiles ride the same AES-GCM channel as all other frames.
Where the two PCs share an IP network (the home Wi-Fi), the pixels do not go
over Bluetooth at all: both sides open a UDP port, exchange addresses in the
BLE handshake, and send to each other at once. Windows Firewall admits UDP that
answers a flow the PC opened itself, so with both sending first each side sees
only replies -- no rule, no administrator -- and the picture runs at 25-30
frames a second (measured 2.5 MB/s). Where the networks do not route to each
other (work), the pixels stay on Bluetooth and the mirror is slower but works.
The window is captured with Windows.Graphics.Capture (the GPU hands over the
window's own surface, ~15 ms for a 4K window, hidden or not); PrintWindow is
the fallback.

Before the machine sleeps Glissando lets go of Bluetooth (advertising, GATT
server, link) and starts over after wake: a live LE server carried through
suspend once left the Intel stack with a dead GATT server that only a restart
of Windows cured. Should that happen anyway, the tray icon turns red and a
balloon says what to do.
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

**Run the copy in `%USERPROFILE%\Program\Glissando`, never the one in `dist\`.**
`dist\` exists to carry the exe to the other PC through OneDrive; copy it out
of there into that PC's `Program\Glissando` and start it from there. A running
exe locks its file, so a Glissando started from the synced `dist\` copy stops
OneDrive from ever replacing it -- that PC silently stays on an old build while
`dist\` on the building PC moves on (RS sat on 0.3.45 for a day that way). At
every start Glissando re-points its own autostart entry to the exe actually
running, so ticking "Start with Windows" once from the right copy is enough.
Both PCs must run the same build; the handshake says so when they do not.
Every build has its own id, `0.3.<commit count>+<hash>` with `.dirty`
appended when the tree had uncommitted changes, plus the build time. It is
shown in the tray menu, the dialog title, the exe's file properties, and,
via the handshake, next to the other PC's name in the tray status, so a
mismatch is visible at once.

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
