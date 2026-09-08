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
tests/LoopIt7.Tests  The feedback guard and the cable end pairing. No window, no devices
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

## Style

No em dashes, no assistant signature. See the notes one directory up.
