# LoopIt7 Cable (driver)

This directory holds the work towards **LoopIt7 Cable**, a virtual audio endpoint pair for
Windows: a playback device that other programs can send audio to, and a recording device that
carries whatever arrived on it.

Read this before you start. The constraints below are Windows', not choices.

## Why the app does not just create one

On macOS, Loopback creates its virtual devices from a kernel extension it ships. Windows works
the same way and does not offer a shortcut:

- An audio endpoint comes from a **kernel mode driver** built on PortCls or AVStream. There is
  no user mode API that adds one. WASAPI, the Core Audio APIs, MMDevice, none of them can.
- Windows 11 x64 will not load a kernel driver unless it is **signed**. For public
  distribution that means Microsoft attestation signing through Partner Center, which needs a
  company account and an EV code signing certificate.
- For local development you can sign it yourself and turn on test signing, at the cost of a
  reboot and a permanent "Test Mode" watermark on the desktop.

So LoopIt7 the application does the parts that do not need a driver, and does them well:

- **Per application capture** with no driver at all, through the process loopback API in
  Windows 10 build 20348 and later. Spotify on its own, a game on its own, everything except
  a game, all without a virtual cable.
- **Device loopback**, so anything a playback device is playing becomes a source.
- **Any output as a destination**, including virtual cables that are already installed
  (VB-Audio Cable, VoiceMeeter, Elgato Virtual Audio, NVIDIA Broadcast). LoopIt7 detects and
  labels these, and points at VB-Audio's free cable when the machine has none.

A cable is only needed for one thing the above cannot do: handing a **mix** back to another
program as if it were a microphone. That is what this driver is for.

## What is here

| Path | What it does |
| --- | --- |
| `scripts/fetch-sample.ps1` | Sparse clones Microsoft's `sysvad` audio driver sample (MIT) into `vendor/` |
| `scripts/brand.ps1` | Rewrites the sample's INF and endpoint names to LoopIt7 Cable |
| `scripts/build.ps1` | Builds the driver with MSBuild and the WDK |
| `scripts/sign-test.ps1` | Makes a self signed test certificate and signs the built driver |
| `scripts/install.ps1` | Installs the signed driver with `pnputil` |

## What still has to be written

Being straight about this: the scaffolding is real, the driver is not finished.

`sysvad` gives a working virtual speaker and a working virtual microphone. It does **not**
connect them: audio written to its render endpoint is discarded, and its capture endpoint
synthesises a tone. Turning that into a cable means writing the piece in the middle:

1. A shared ring buffer in the driver, one per cable instance.
2. `CMiniportWaveRTStream::GetPosition` and the render side writing into that ring.
3. The capture side reading from the same ring, with a fixed offset for safety, and filling
   silence on underrun.
4. Rate and format agreement between the two endpoints, since a cable that resamples inside
   the kernel is a cable nobody wants.
5. Multiple cable instances, so "LoopIt7 Cable 1" and "LoopIt7 Cable 2" can exist side by side.

That is a real driver project, not a patch. The scripts here get you to the point where you
can open the solution and start on it.

## Requirements

- Visual Studio 2022 Build Tools with the **Desktop development with C++** workload
- The **Windows Driver Kit** matching your Windows SDK version
- Administrator rights for installing the WDK, for test signing, and for installing the driver

## Local development, start to finish

```powershell
# 1. Fetch the Microsoft sample
powershell -ExecutionPolicy Bypass -File driver\scripts\fetch-sample.ps1

# 2. Apply LoopIt7 naming
powershell -ExecutionPolicy Bypass -File driver\scripts\brand.ps1

# 3. Build (needs the WDK)
powershell -ExecutionPolicy Bypass -File driver\scripts\build.ps1

# 4. Make a test certificate and sign the output
powershell -ExecutionPolicy Bypass -File driver\scripts\sign-test.ps1

# 5. Turn on test signing, as Administrator, then reboot
bcdedit /set testsigning on

# 6. After the reboot, install it, as Administrator
powershell -ExecutionPolicy Bypass -File driver\scripts\install.ps1
```

To go back to normal: `pnputil /delete-driver <oem#.inf> /uninstall`, then
`bcdedit /set testsigning off`, then reboot.

## Shipping it to other people

Test signing is for your machine only. For anyone else:

1. Get an EV code signing certificate from a certificate authority.
2. Register for a Microsoft Partner Center hardware account.
3. Submit the driver package for **attestation signing**.
4. Ship the returned, Microsoft signed package.

Until that is done, LoopIt7's installer does not offer this driver, and the application points
at VB-Audio's already signed cable instead. That is the honest thing to do: a download that
tells people to disable a security feature is not a product.

## Licensing

The Microsoft sample under `vendor/` is MIT licensed and is not committed to this repository.
`fetch-sample.ps1` pulls it on demand. Everything written for LoopIt7 is MIT, same as the app.

---

Built by [SeventhSG](https://github.com/SeventhSG).
