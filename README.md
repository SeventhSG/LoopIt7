<div align="center">

<img src="assets/banner.png" alt="LoopIt7" width="100%">

### Every mic, every program, every output. One canvas. You draw the cables.

**A free patchbay for Windows audio, with a mixer on every connection.**

[![Build](https://img.shields.io/github/actions/workflow/status/SeventhSG/LoopIt7/build.yml?branch=main&style=flat-square&label=build&color=E8A33D&labelColor=1A1714)](https://github.com/SeventhSG/LoopIt7/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/SeventhSG/LoopIt7?style=flat-square&color=E8A33D&labelColor=1A1714)](https://github.com/SeventhSG/LoopIt7/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/SeventhSG/LoopIt7/total?style=flat-square&color=E8A33D&labelColor=1A1714)](https://github.com/SeventhSG/LoopIt7/releases)
[![License](https://img.shields.io/github/license/SeventhSG/LoopIt7?style=flat-square&color=E8A33D&labelColor=1A1714)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-E8A33D?style=flat-square&labelColor=1A1714)](#requirements)

## [⬇ Download LoopIt7](https://github.com/SeventhSG/LoopIt7/releases/latest)

Free. Open source. About 3 MB.

</div>

---

<img src="assets/patchbay.png" alt="The LoopIt7 patchbay: sources on the left, outputs on the right, cables between them" width="100%">

## Get started in two minutes

1. **Download** `LoopIt7-Setup.exe` from [Releases](https://github.com/SeventhSG/LoopIt7/releases/latest) and run it.
2. **Keep "Install Virtual Audio Cable Lite" ticked.** Its own installer opens partway through;
   click through it. Setup waits, then names the cable **LoopIt7 Cable** for you and tells you
   when it is ready.
3. **Open LoopIt7**, press **Add source** and **Add output**, and drag a cable between them.
4. **Turn your volume down**, then press **Start routing**.

That is it. Already have LoopIt7? Run the same setup file: it updates in place and keeps your
pages, presets and options exactly as they were.

### Requirements

- Windows 11, or Windows 10 build 20348 and later
- The .NET 10 desktop runtime. Setup fetches it for you if it is missing.

### "Windows protected your PC"

LoopIt7 is not code signed yet, so SmartScreen shows a blue warning the first time. Press
**More info**, then **Run anyway**. On a PC with **Smart App Control** switched on, Windows
blocks unsigned apps outright; getting LoopIt7 signed is in progress. Until then, every release
is built in the open by [GitHub Actions](https://github.com/SeventhSG/LoopIt7/actions) from the
tagged commit in this repository, so you can see exactly what you are running.

## What it is

Windows plays audio to one output at a time and never shows you where anything is going.
LoopIt7 puts every microphone, speaker, headset, interface and running program on one canvas,
and you draw the connections yourself.

- **Your mic in your headphones and on the room monitors**, at the same time.
- **Spotify into Discord** without it going through your microphone.
- **The game on stream but not in your ears.**
- **Your DAW into OBS**, through a cable that carries LoopIt7's name.

The meters move while it happens, so you always see which cable is carrying what.

## How to use it

| Button | What it puts on the page |
| --- | --- |
| **Add source** | A microphone, an audio interface input, everything a device is playing, or **one single program** on its own. |
| **Add output** | Any speaker, headset, interface or virtual cable. |
| **Add cable** | The **LoopIt7 Cable**, listed at the top. **In** hears what a program plays into it, so a DAW or game lands on your canvas. **Out** sends a mix into it, so Discord or OBS can pick it as a microphone. |

Then drag from a source to an output. Every box and every cable has its own **fader, mute,
balance and meter**. Cables also get a trim and an alignment delay, because the speaker across
the room is further away than the one on your desk. Solo a source and everything else drops
out, like on a real mixing desk.

**Pages** work like sheets in a spreadsheet: keep a streaming setup, a monitoring setup and a
rehearsal setup side by side and flip between them with the tabs at the bottom.

**Can't hurt your ears.** A connection that would loop a device back into itself is refused
before it is made, not just warned about, because a feedback loop reaches full volume in under
a second in whatever you are wearing.

<div align="center">
<img src="assets/devices.png" alt="The Devices workspace listing every endpoint with its format" width="49%">
<img src="assets/midi.png" alt="The MIDI workspace" width="49%">
</div>

| Workspace | What lives there |
| --- | --- |
| **Patchbay** | The canvas. Boxes you drag, ports you pull cables from, meters that move. |
| **Devices** | Every endpoint with its sample rate, bit depth and channels. Set the default device or the shared format without opening three dialogs. |
| **MIDI** | Route any MIDI input to any set of MIDI outputs. The half of Apple's Audio MIDI Setup Windows never had a window for. |

Plus what a tool you leave open all day needs: a tray icon, start with Windows, a global mute
hotkey, presets, two ready made starting setups, and reconnecting on its own when a device is
unplugged and plugged back in.

---

## 🤓 For the nerds

Everything below is how it works under the hood. None of it is needed to use LoopIt7.

### The engine

The engine is a graph. Each source opens its own WASAPI capture and converts to stereo float at
its own rate. Each cable owns a ring buffer, a delay line and a fader, and resamples once on its
way to the destination it belongs to. Each destination sums its incoming cables and hands the
result to WASAPI on that endpoint's own clock.

```
source ──┬─ cable ─ delay ─ trim ─ resample ─┐
         │                                   ├─ mix ─ channel map ─ WASAPI ─ output
source ──┴─ cable ─ delay ─ trim ─ resample ─┘
```

Nothing shares a clock. That is what lets a 44.1 kHz interface and a 48 kHz headset run off the
same microphone without one stalling the other, and it is why cables trim their own queues:
two independent clocks drift, and untrimmed drift becomes a monitor mix a quarter second late.

Single program capture needs no driver at all. It uses the process loopback API Windows has
shipped since build 20348, which is why that is the minimum Windows 10 build.

### Why a cable needs a driver

On Windows, an audio device comes from a kernel mode driver. No user mode API adds one, and
Windows only loads a kernel driver that **Microsoft** has signed. An ordinary code signing
certificate is not enough: the loader still refuses it with error 52. Check the signer, not the
status, before trusting any "signed" community driver.

So LoopIt7 does everything it can without a driver (per program capture, device loopback, any
output as a destination) and, for the one thing that needs one, bundles
[Virtual Audio Cable Lite](https://vac.muzychenko.net/en/) by Eugene Muzychenko. Its driver is
signed by Microsoft, and its licence allows handing out the Lite version with a free program,
unmodified. VAC Lite is free for private, non-commercial use; if you earn money with it, buy a
full VAC licence from its author.

### What the cable is called

Setup names the cable it installed **LoopIt7 Cable** before it finishes, because that name is
the only handle a DAW or Discord has on it. The driver itself is untouched: LoopIt7 changes the
name Windows shows, and writes down the original so uninstalling puts it back.

A cable that was already on your PC keeps its own name. It is written into somebody's OBS scene
and Discord settings, and LoopIt7 has no business renaming it behind their back. The Devices
page offers to take one over if you ask, and names the device it would change.

A cable has two ends, and Windows never says which belongs to which. That is the quietest way a
routing setup fails, so LoopIt7 pairs them for you and says so in plain words.

### A driver of our own

[`driver/`](driver/README.md) holds **LoopIt7 Cable**, a first party virtual audio driver built
on Microsoft's SysVAD sample: a lock protected ring with a cursor per reader, one fixed format,
and a cable only INF. It has been round trip tested on real hardware with exact level and no
dropouts. Shipping it needs Microsoft attestation signing, which is why setup bundles VAC Lite
for now.

### Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), plus
[Inno Setup](https://jrsoftware.org/isdl.php) for the installer.

```powershell
git clone https://github.com/SeventhSG/LoopIt7.git
cd LoopIt7

dotnet build src\LoopIt7\LoopIt7.csproj -c Release   # app only
dotnet run --project tests\LoopIt7.Tests             # routing safety rules, headless
powershell -ExecutionPolicy Bypass -File build\build.ps1   # tests, artwork, publish, installer
```

`build\build.ps1` refuses to publish if the safety tests fail, and downloads VAC Lite for the
installer, checked against a pinned hash. `build\make-assets.ps1` draws the icon, banner and
installer artwork from one set of geometry.

Something not working? `LoopIt7.exe --self-test` writes a report to
`%AppData%\LoopIt7\self-test.txt` saying what your PC supports and whether capture works.

## Licence

MIT. See [LICENSE](LICENSE).

Built on [NAudio](https://github.com/naudio/NAudio) by Mark Heath. The bundled cable is
[Virtual Audio Cable Lite](https://vac.muzychenko.net/en/) by Eugene Muzychenko, under its own
licence.

---

<div align="center">

**Built by [SeventhSG](https://github.com/SeventhSG)**

</div>
