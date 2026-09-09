# LoopIt7

A patchbay for Windows audio. Sources, destinations and the cables between them on one
canvas, with a mixer on every box.

The product has three pillars: **virtual audio driver, routing engine, mixer UI**. The
engine and the mixer are done. The driver is not, and the reason is in `driver/README.md`.

## Who it is for

Musicians and streamers, not any one machine. Nothing may hard code a device id where a
role would do. The starting templates in `MainViewModel.ApplyTemplate` resolve devices by
role at the moment they run, and new features should work the same way: a laptop with one
headset and a rig with an interface both have to land somewhere sensible.

## Safety rule

**Never start routing yourself when testing.** Build, screenshot, drive the interface, but
leave Start routing to the user and tell them to turn the output down first. A patch that
sends a playback device's loopback back into that same device reaches full scale in under
a second, in whatever the user is wearing.

The app guards against that case in `FeedbackGuard.WouldFeedBack`, called from
`MainViewModel.TryConnect`, which refuses the connection rather than warning. Keep that
guard, and extend it if a new node type can create the same loop. It covers three shapes
now: a chain of virtual outputs closing on itself, a loopback tap reaching its own device,
and a cable whose recording end comes back round to its own playback end. Each one has a
check in `tests\LoopIt7.Tests`, and `build.ps1` refuses to publish if they fail.

## Layout

```
src/LoopIt7/
  Audio/
    Graph/          AudioGraph, SourceNode, DestinationNode, Connection, MixSampleProvider
    Interop/        WASAPI process loopback, the per application capture path
    *SampleProvider Gain with pan, delay line, channel map
    DeviceService   Endpoint enumeration and change notifications
    EndpointNaming  Renames an endpoint; CableOwnership decides which ones we may rename
    EndpointControl IPolicyConfig: default device, shared engine format
    VirtualCableService  Detects installed cables and pairs their two ends
  Midi/             MIDI input to output routing
  ViewModels/       MainViewModel is the mixer; PatchNodeViewModel is one box
  Views/            PatchbayView is the canvas, plus Devices and MIDI
  Themes/           Tokens.xaml holds the palette and the shape rules
tests/LoopIt7.Tests  Feedback guard, cable end pairing, who may rename what. Headless
driver/             Scaffolding towards a LoopIt7 Cable, and what it would take
build/              make-assets.ps1 draws every raster; build.ps1 does the whole pipeline
```

## Engine shape

Each source opens its own WASAPI capture and converts to stereo float at its own rate.
Each cable owns a ring buffer, a delay line and a fader, and resamples once on its way to
the destination it belongs to. Each destination sums its cables and hands the result to
WASAPI on that endpoint's own clock.

Nothing shares a clock. That is what lets a 44.1 kHz interface and a 48 kHz headset run off
one microphone without either stalling the other, and it is why cables trim their own
queues: two independent clocks drift, and untrimmed drift becomes a late monitor mix.

## Commands

```powershell
dotnet build src\LoopIt7\LoopIt7.csproj -c Release       # app only
dotnet run --project tests\LoopIt7.Tests                 # routing safety rules, headless
powershell -File build\build.ps1                         # tests, artwork, publish, installer
powershell -File build\screenshot.ps1 -Out assets\x.png  # capture the running window
LoopIt7.exe --self-test                                  # writes %AppData%\LoopIt7\self-test.txt
```

`screenshot.ps1` uses PrintWindow, so it never captures whatever else is on screen.

## The driver, honestly

A Windows audio endpoint comes from a kernel mode driver. No user mode API adds one, and
the loader will not accept a driver without a **Microsoft attestation** signature. An
ordinary code signing certificate is not enough: the community Virtual-Audio-Driver is
signed by SignPath Foundation via GlobalSign, Authenticode calls it valid, and the loader
still refuses it with error 52. Verify any such claim with `Get-AuthenticodeSignature` and
read the signer, not the status.

Until that is solved, LoopIt7 does everything that needs no driver, and for the one thing
that does, it uses whatever cable is already installed and names both of its ends for the
user.

A cable is also the only way anything in Windows can play *into* LoopIt7. Add source offers
every cable pairing under "From another program", and picking one puts a source box on the
canvas fed by the cable's recording end: that is the half a DAW wants, and it is a plain
source, patchable anywhere a microphone is. A virtual output can be given the same way in by
binding it to a cable instead, when what arrives should be summed with other programs before
it leaves. One cable serves one box either way, so how many ways in Windows can see is how
many cables are installed.

The installer bundles VB-Audio's cable when their redistributable is sitting in
`installer/cable/`, and builds and works without it when it is not, so the licensing answer
changes a file rather than the script. Shipping that file needs a distribution agreement
with VB-Audio. The offer appears whenever that one cable is missing, even on a machine with
cables of another make: those belong to somebody else's setup and are not ours to rename, so
without one of our own LoopIt7 could never put its name on anything. The exception is that
same cable already being present, where running its installer again would add nothing and
hand us the user's own cable.

When setup installs one it writes the driver string to `installed-cable.txt` beside the
settings, because by the time the app runs, a cable installed thirty seconds ago and one the
user has had for years look identical. The driver string and not the vendor: VB-Audio's plain
cable, their A+B pack and VoiceMeeter's VAIO all say VB-Audio, and installing one is no
licence to rename the others.

Which cables LoopIt7 may rename is decided in `CableOwnership`, and the rule is narrow on
purpose. A cable that was on the machine before LoopIt7 ever asked for one is never
renamed *on its own*, because other people's OBS scenes and Discord settings point at that
name. Only a cable that arrived *after* the user followed LoopIt7's own prompt to get one, or
that our own setup installed, is ours to name unasked, and the uninstaller runs
`LoopIt7.exe --release-cables` to put every name back.

A cable that is ours gets named `CableOwnership.DefaultName`, "LoopIt7 Cable", the first time
the app sees it, rather than waiting to be wired to something. The name is the only handle a
DAW or Discord has on the way in, and a cable sitting there called "CABLE Input" is a way in
nobody was told to look for. Binding it to a virtual output renames it after that box.

There is exactly one door through the narrow rule, `CableOwnership.Adopt`, and it only opens
from the user's side: the Devices page offers to take over a cable that was here first, naming
the device it would change and saying the old name comes back on uninstall. It exists because
a machine that already has VB-CABLE never gets one from our installer either, so without it
the person most likely to want LoopIt7's name is the one who could never have it. Nothing
automatic may call it. `ReleasedCableIds` is the other half of that promise: a name given back
stays given back, or the next device refresh would put it straight on again.

## Style

No em dashes, no assistant signature. See the notes one directory up.
