<div align="center">

<img src="assets/banner.png" alt="LoopIt7" width="100%">

**A patchbay for Windows audio. Every source, every output, and the cables in between.**

[![License](https://img.shields.io/github/license/SeventhSG/LoopIt7?style=flat-square&color=E8A33D&labelColor=1A1714)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-E8A33D?style=flat-square&labelColor=1A1714)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-E8A33D?style=flat-square&labelColor=1A1714)](#build-it-yourself)
[![Download](https://img.shields.io/badge/Download-setup.exe-E8A33D?style=flat-square&labelColor=1A1714)](https://github.com/SeventhSG/LoopIt7/releases/latest)

</div>

---

<img src="assets/patchbay.png" alt="The LoopIt7 patchbay, with a microphone, a system audio tap and Discord routed to two outputs" width="100%">

## What it is

Windows gives you one output at a time and no way to see where your audio is going. LoopIt7
puts every microphone, speaker, headset, interface and running program on one canvas and lets
you draw the connections yourself.

One microphone can reach your headphones and the room monitors at once. Spotify can go to
Discord without going through your mic. A game can go to the stream but not to your ears. The
meters move while it happens, so you can see which cable is carrying what.

## What it does

**Sources.** Any recording endpoint, any playback endpoint tapped in loopback, and any single
running program. That last one is the interesting part: LoopIt7 captures one application on
its own, with no virtual cable and no driver, using the process loopback API Windows has
shipped since build 20348. Spotify without the game. The game without Discord.

**Destinations.** Any playback endpoint, including software cables that are already installed.
VB-Audio Cable, VoiceMeeter, Elgato Virtual Audio and NVIDIA Broadcast are recognised and
labelled, so you can tell a real speaker from a pipe to another program.

**Cables.** One source can feed many outputs. One output can sum many sources. Every cable has
its own trim, mute and alignment delay, because the speaker across the room is further away
than the one on your desk.

| Workspace | What lives there |
| --- | --- |
| **Patchbay** | The canvas. Boxes you drag, ports you pull cables from, meters that move. |
| **Devices** | Every endpoint on the machine, with its sample rate, bit depth and channel count. Set the default device or change the shared engine format without opening three dialogs. |
| **MIDI** | Route any MIDI input to any set of MIDI outputs. The half of Apple's Audio MIDI Setup that Windows never had a window for. |

Plus the things a tool you leave open all day needs: presets, a tray icon, start with Windows,
a global mute hotkey, and reconnection on its own when a device is unplugged and plugged back
in.

<div align="center">
<img src="assets/devices.png" alt="The Devices workspace listing every endpoint with its format" width="49%">
<img src="assets/midi.png" alt="The MIDI workspace" width="49%">
</div>

## About virtual devices, honestly

On macOS, Loopback creates its own virtual devices. It can do that because it ships a kernel
extension. Windows works the same way and offers no shortcut.

An audio endpoint on Windows comes from a kernel mode driver. There is no user mode API that
adds one, and Windows 11 x64 will not load an unsigned driver. Any program claiming otherwise
is either installing a driver of its own or using one that is already there.

So LoopIt7 does not pretend. It does everything that does not need a driver, and it does it
without asking you to install anything:

- Per application capture, driver free.
- Device loopback, driver free.
- Any output as a destination, driver free.

The one thing a cable is genuinely needed for is handing a **mix** back to another program as
if it were a microphone. For that, LoopIt7 uses whatever cable is already on your machine, and
points you at [VB-Audio's free one](https://vb-audio.com/Cable/) if there is none.

Work towards a LoopIt7 cable of our own lives in [`driver/`](driver/README.md), along with a
plain account of what it needs: a WDK, test signing for development, and Microsoft attestation
signing before it can reach anybody else.

## Install

Grab `LoopIt7-Setup.exe` from [Releases](https://github.com/SeventhSG/LoopIt7/releases/latest)
and run it. It installs for the current user, so there is no admin prompt, and it fetches the
.NET 10 desktop runtime if the machine does not have it.

### Requirements

- Windows 11, or Windows 10 build 20348 and later for per application capture
- The .NET 10 desktop runtime, which the installer offers to fetch

## How it works

The engine is a graph. Each source opens its own WASAPI capture and converts to stereo float at
its own rate. Each cable owns a ring buffer, a delay line and a fader, and resamples once on
its way to the destination it belongs to. Each destination sums its incoming cables and hands
the result to WASAPI on that endpoint's own clock.

Nothing shares a clock, which is what lets a 44.1 kHz interface and a 48 kHz headset run off
the same microphone without one stalling the other. Cables trim their own queues so drift
between two independent clocks never turns into a monitor mix that is a quarter second late.

```
source ──┬─ cable ─ delay ─ trim ─ resample ─┐
         │                                   ├─ mix ─ channel map ─ WASAPI ─ output
source ──┴─ cable ─ delay ─ trim ─ resample ─┘
```

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the
installer you also need [Inno Setup](https://jrsoftware.org/isdl.php).

```powershell
git clone https://github.com/SeventhSG/LoopIt7.git
cd LoopIt7

# App only
dotnet build src\LoopIt7\LoopIt7.csproj -c Release

# Artwork, publish and installer, all the way to dist\
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

`build\make-assets.ps1` draws the icon, the banner and the installer artwork from one set of
geometry, so the mark is never redrawn by hand. `build\screenshot.ps1` captures the running
window for this page.

If something goes wrong, `LoopIt7.exe --self-test` writes a report to
`%AppData%\LoopIt7\self-test.txt` saying what this machine supports and whether capture works.

## Licence

MIT. See [LICENSE](LICENSE).

Built on [NAudio](https://github.com/naudio/NAudio) by Mark Heath.

---

<div align="center">

**Built by [SeventhSG](https://github.com/SeventhSG)**

</div>
