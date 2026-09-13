# LoopIt7 Cable (driver)

**LoopIt7 Cable** is a virtual audio cable for Windows: a playback device called **Cable In**
that any program can play into, and a recording device called **Cable Out** that carries
exactly what arrived. It is built on Microsoft's `sysvad` audio driver sample (MIT).

Read this before you start. The signing constraints below are Windows', not choices.

## Status

| | |
| --- | --- |
| Builds from a clean checkout | yes, `fetch-sample.ps1` then `build.ps1` |
| INF validation (InfVerif `/w`, `/h`, `/k`) | clean |
| Static analysis (WDK code analysis) | nothing in LoopIt7's code; the sample's remaining findings are listed below |
| Loaded and round trip tested on real hardware | **not yet** with this build |
| Signed for other people's machines | **no**: needs Microsoft attestation signing, see below |

Earlier builds crashed the development machine three times (bugcheck `0xD1`). All three were
root caused from the crash dumps and are fixed; see "What went wrong before".

## How it works

The sample already simulates a playback device and a recording device, each driven by a timer
that moves a position through its audio buffer the way hardware would. It did not connect
them: what was played was thrown away, and the recording side made up a test tone.

LoopIt7 Cable adds the piece in the middle, `CCableRing` (`EndpointsCommon/CableRing.*` in the
patched sample):

- One ring buffer (500 ms) per device, owned by the adapter object that both endpoints share.
- The playback stream copies each stretch of audio it has "played" into the ring. Only one
  playback stream can write: the Cable In filter allows a single stream in a single mode.
- Every recording stream reads with its own cursor, so two programs recording Cable Out both
  get everything. A reader sits a small cushion (30 ms or more) behind the writer, fills silence
  when it runs dry rather than replaying old audio, and resyncs if it falls too far behind.
- Both ends accept exactly one format, **48 kHz, 16 bit, stereo**. Windows converts to that on
  the way in and out, so the driver never resamples.
- Everything on that path runs at `DISPATCH_LEVEL`, so it lives in non-paged code and takes a
  spin lock, never anything that can block.

It also switches off the parts of the sample a cable must not have: the extra simulated
devices, audio effects registration, the DRM claim, the Bluetooth and USB headset bypass (which
watches for real headsets and makes endpoints for them), and the debug feature that writes
everything played to a file.

## What is here

| Path | What it does |
| --- | --- |
| `patches/loopit7-cable.patch` | Everything that turns the sample into LoopIt7 Cable |
| `scripts/fetch-sample.ps1` | Fetches the sample at a pinned commit into `vendor/` and applies the patch |
| `scripts/build.ps1` | Clean rebuild, InfVerif, stages the package, makes the catalog |
| `scripts/sign-test.ps1` | Makes a self signed test certificate and signs the catalog |
| `scripts/install.ps1` | Removes any old copy, installs, and proves the running binary is this build |
| `scripts/uninstall.ps1` | Removes the device, the package and the service. Also the recovery step |
| `scripts/export-patch.ps1` | Saves driver edits made in `vendor/` back into the patch |
| `scripts/DefaultEndpoints.ps1` | Puts your default playback and recording devices back if install moved them |
| `tools/CableCheck` | Round trip test: plays a tone into Cable In, measures what comes out of Cable Out |

`vendor/` is not committed. Edit the driver there, then run `export-patch.ps1` and commit the
patch, or the change exists only on your machine.

## Requirements

- Visual Studio 2022 Build Tools with the **Desktop development with C++** workload
- The **Windows Driver Kit** matching your Windows SDK (built and tested with 10.0.26100)
- Windows 11 22H2 (build 22621) or later: the INF targets nothing older
- Administrator rights for test signing and for installing the driver

## Local development, start to finish

```powershell
# 1. Fetch the pinned Microsoft sample and apply LoopIt7 Cable
powershell -ExecutionPolicy Bypass -File driver\scripts\fetch-sample.ps1

# 2. Build, validate the INF, make the catalog
powershell -ExecutionPolicy Bypass -File driver\scripts\build.ps1

# 3. Make a test certificate and sign the catalog
powershell -ExecutionPolicy Bypass -File driver\scripts\sign-test.ps1

# 4. Turn Secure Boot off in the firmware setup. Then, as Administrator, and reboot:
bcdedit /set testsigning on

# 5. After the reboot, as Administrator
powershell -ExecutionPolicy Bypass -File driver\scripts\install.ps1

# 6. Check it end to end (close LoopIt7 first, and turn your output down)
dotnet run --project driver\tools\CableCheck -c Release
```

Install after the machine has booted with test signing on, not before the reboot, so the driver
first loads while you are watching rather than in the middle of a boot. That does not stop a
crash loop: the device stays installed across reboots, so a driver that crashes once will crash
on every boot. The way out of that is below, and it works without getting to a prompt first.

### Riot Vanguard and test signing

On a machine with Riot Vanguard (Valorant's anti-cheat) installed, the `testsigning` flag was
found deleted from the boot configuration after a reboot, with Secure Boot off. Vanguard is the
likely cause; it refuses to run in test mode. The way round it, and the safer way to test in
any case: **Shift+Restart > Troubleshoot > Advanced options > Startup Settings > Restart**, then
press **7** ("Disable driver signature enforcement"). That lasts one boot. `install.ps1` checks
the running boot's options, so it accepts either. This is how the round trip test passed.

### If something goes wrong

Turn **Secure Boot back on** in the firmware setup. Windows then refuses test signed drivers,
so the cable cannot load and the machine boots normally. Then, as Administrator:

```powershell
powershell -ExecutionPolicy Bypass -File driver\scripts\uninstall.ps1
```

To go back to normal completely afterwards: `bcdedit /set testsigning off`, reboot.

Crash dumps are in `C:\Windows\Minidump`. `cdb.exe` from the Windows SDK reads them against
the `.pdb` that `build.ps1` leaves next to the driver.

## What went wrong before

Three crashes, one bug and two process mistakes:

1. **The bug.** The sample saves played audio to a file only when a registry flag asks for it,
   and never initialises the file writer otherwise. The first cable build called the writer
   unconditionally to reach the ring buffer code beside it, which dereferenced NULL at
   `DISPATCH_LEVEL` on the first timer tick: bugcheck `0xD1` in `CSaveData::WriteData`. The
   cable now has its own path, and the file writer can no longer be switched on at all.
2. **A fix that was never installed.** Rebuilding changes the files in the build folder, not the
   copy Windows already bound to the device. `install.ps1` now removes the old copy first and
   fails unless the running binary's SHA-256 matches the one just built.
3. **A fix that was never compiled in.** The driver project links the shared library by path,
   so building the driver alone reused a stale library. `build.ps1` now does a clean rebuild of
   both and refuses output older than the build.

## Remaining analysis findings (the sample's, not reached by the cable)

- `adapter.cpp` 798, 823: a deliberate API self test the sample runs at start up, passing a
  documented optional NULL.
- `common.cpp` 108: `m_PowerRelations` is initialised in `Init`, not the constructor.
- `common.cpp` 2453: a false positive in the power relations buffer size arithmetic.
- `AudioModule*.h`, `AudioModuleHelper.cpp`: audio module code; the cable declares no modules.
- `BthhfpDevice.*`, `UsbHsDevice.*`: headset bypass code, switched off in the cable.

Two latent bugs the analysis found in the sample's registry copy helpers (a wrong NULL check
and a double free) are fixed in the patch, although the cable never calls them.

## Limits of this version

- One cable. Several ("LoopIt7 Cable 1", "2") needs more than one device instance and a ring
  per instance.
- One format, 48 kHz / 16 bit / stereo. Programs asking for anything else are converted by
  Windows in shared mode; exclusive mode must use exactly that format.
- Windows may make Cable In the default playback device when it appears. `install.ps1` puts the
  previous defaults back; an installer would have to do the same.

## Shipping it to other people

Test signing is for your machine only. For anyone else:

1. Get an EV code signing certificate from a certificate authority.
2. Register for a Microsoft Partner Center hardware account.
3. Submit the driver package (`build.ps1`'s `Package` folder) for **attestation signing**.
4. Ship the returned, Microsoft signed package.

Until that is done, LoopIt7's installer does not offer this driver, and the application points
at VB-Audio's already signed cable instead. That is the honest thing to do: a download that
tells people to disable a security feature is not a product.

## Licensing

The Microsoft sample under `vendor/` is MIT licensed and is not committed to this repository;
`fetch-sample.ps1` pulls it on demand. The patch, the scripts and CableCheck are MIT, same as
the app. `devcon`, used by `install.ps1`, is built from the same MIT samples repository.

---

Built by [SeventhSG](https://github.com/SeventhSG).
