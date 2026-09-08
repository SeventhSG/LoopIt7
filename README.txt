===============================================================================

  L O O P I T 7
  A patchbay for Windows audio. Name your own outputs, send programs to
  them, and fan them out anywhere.

  Version 1.2.0
  Built by SeventhSG
  https://github.com/SeventhSG/LoopIt7

===============================================================================


WHAT IT IS
-------------------------------------------------------------------------------

Windows gives you one output at a time and no way to see where your audio is
going. LoopIt7 puts every microphone, speaker, headset, interface and running
program on one canvas and lets you draw the connections yourself.

One microphone can reach your headphones and the room monitors at once.
Spotify can go to Discord without going through your mic. A game can go to the
stream but not to your ears. The meters move while it happens, so you can see
which cable is carrying what.


VIRTUAL OUTPUTS
-------------------------------------------------------------------------------

Make an output of your own, name it, and decide what comes out of it.

  1. Press "Add virtual output" and name it after whatever it is for.
     Discord. Stream. Speakers in the other room.
  2. Press the + on the box and pick the programs that should play through it.
  3. Drag cables from the box to every output that should hear them.

Each program you assign gets captured on its own and muted where Windows was
sending it, so it comes out of your named box instead. The box has one fader,
one mute and one meter for the whole thing, so moving where all of it goes is
one cable rather than three programs' settings.

    Discord  ---+
    Spotify  ---+---> [ Stream ] ---+---> Headphones
    Mic      ---+                   +---> Elgato Virtual Audio ---> OBS

Virtual outputs can feed each other. A chain that would close on itself is
refused before it can run away.

A virtual output is a box inside LoopIt7, not a Windows device. Nothing else on
the machine can select it from a dropdown, which is exactly why you assign
programs to it here rather than there. See ABOUT VIRTUAL DEVICES below.

Turning it off: the toggle in the corner of a program's box is what takes it
off its own output. Switch it off and the program plays in both places again.
Everything is handed back the moment routing stops or LoopIt7 closes, so
nothing is ever left muted in your volume mixer.


GETTING STARTED
-------------------------------------------------------------------------------

The fastest way in: an empty patchbay offers two starting points.

  STREAMING SETUP    Your microphone and your desktop audio, into your own
                     headphones and into a cable for OBS, Discord or Zoom.

  MONITORING SETUP   One microphone reaching your headphones and the room at
                     the same time.

Both are built from whatever this machine has, so they land somewhere sensible
on any rig. Change whatever does not fit.

To build one by hand:

  1. Press "Add source" and pick a microphone, a playback device to tap, or a
     running program.
  2. Press "Add output" and pick where it should land.
  3. Drag from the circle on the right of the source to the output box.
  4. Press "Start routing".

Sources sit on the left, virtual outputs in the middle, real outputs on the
right.

Drag the boxes anywhere you like. The layout is saved with your setup.


THE MIXER
-------------------------------------------------------------------------------

Every source and every output has:

  Fader     -60 dB to +12 dB.
  Mute      The speaker button.
  Solo      The S button, on sources. Everything else drops while it is on.
            "Clear solo" appears next to the transport while soloing.
  Balance   The thin slider underneath. Right click to recentre.
  Meter     Peak with a hold marker. It turns red when the signal clips.

One source can feed many outputs. One output sums every source patched into it.


A NOTE ON FEEDBACK
-------------------------------------------------------------------------------

Tapping a playback device and sending it back to that same device is a feedback
loop. It reaches full scale in under a second, and it does that in whatever you
are wearing. LoopIt7 refuses that connection rather than warning about it.

That holds however many boxes are in the way. A device tap that reaches its own
device through two virtual outputs is refused just the same, and so is a chain
of virtual outputs that closes on itself.

When you are trying an unfamiliar routing, turn your output down first anyway.


THE THREE WORKSPACES
-------------------------------------------------------------------------------

  PATCHBAY   The canvas. Boxes you drag, ports you pull cables from, meters
             that move in real time. Click a cable to trim it, mute it, or
             delay it.

  DEVICES    Every endpoint on the machine with its sample rate, bit depth and
             channel count. Set the default device or change the format the
             Windows audio engine runs it at, without opening three dialogs.

  MIDI       Route any MIDI input to any set of MIDI outputs. Routing starts
             the moment you switch a destination on.


CAPTURING A SINGLE PROGRAM
-------------------------------------------------------------------------------

LoopIt7 can capture one application on its own, with no virtual cable and no
driver at all. Spotify without the game. The game without Discord.

Only programs Windows currently lists as playing appear in the menu, so start
playback first, then open "Add source".

This needs Windows 10 build 20348 or later.


ABOUT VIRTUAL DEVICES
-------------------------------------------------------------------------------

On macOS, Loopback creates its own virtual devices, because it ships a kernel
extension. Windows works the same way and offers no shortcut: an audio endpoint
comes from a kernel mode driver, there is no user mode API that adds one, and
Windows 11 x64 will not load an unsigned driver.

So LoopIt7 does not pretend. Everything above works with no driver installed,
including the virtual outputs. On its own, a LoopIt7 virtual output is a box
inside the app: it will not appear in the Windows sound settings. Programs
reach it by being assigned to it here instead of choosing it there.


GIVING A BOX A WAY IN
-------------------------------------------------------------------------------

If you want other programs to choose your box themselves, press the link button
on it and pick a cable.

A cable is a free driver with two ends. The other program sends to one end,
LoopIt7 listens on the other, and what arrives lands in your box and leaves
down its cables as usual. The picker lists the two ends as a pair, so nothing
is guessed, and it says which end LoopIt7 will open.

If there is no cable on this machine the picker says so and offers to fetch
one. LoopIt7 recognises VB-Audio Cable, VoiceMeeter, Elgato Virtual Audio and
NVIDIA Broadcast, and points you at VB-Audio's free cable if you have none:

    https://vb-audio.com/Cable/

A cable LoopIt7 asked you to install is named after the box it serves, so the
name in Discord's list is the name on your canvas. A cable that was already on
the machine keeps the name it came with, because other programs are pointing at
that name and it is not ours to change. Uninstalling LoopIt7 puts back every
name it changed.


CABLE CONTROLS
-------------------------------------------------------------------------------

Click a cable on the canvas to open its controls.

  Trim     Level for this cable only, separate from the source and the output.
  Mute     Silences this one cable. The source keeps feeding its other cables.
  Delay    0 to 500 ms. Push a distant speaker back in time to meet the others.
           Hold Shift while pressing + or - to move in steps of ten.


BUFFER SIZE
-------------------------------------------------------------------------------

The buffer control at the bottom right sets the WASAPI buffer per endpoint.

   3 ms    Tightest. Needs a quiet machine and good drivers.
   5 ms    Tight.
  10 ms    Default. A good balance for most machines.
  20 ms    Safer under load.
  40 ms    Safest. Use this if you hear clicks or dropouts.


KEYBOARD AND TRAY
-------------------------------------------------------------------------------

  Ctrl + Alt + M     Mute every source at once, from anywhere.

Closing the window hides LoopIt7 to the tray and keeps routing. Quit from the
tray icon to stop it properly. Both behaviours can be changed under Options.


WHERE THINGS ARE KEPT
-------------------------------------------------------------------------------

  %AppData%\LoopIt7\settings.json    Your patchbay, presets and options
  %AppData%\LoopIt7\crash.log        Written only if something goes wrong
  %AppData%\LoopIt7\self-test.txt    Written by LoopIt7.exe --self-test

If something is not working, run LoopIt7.exe --self-test from a command prompt
and read the report. It says what this machine supports and whether capture is
actually working.


TROUBLESHOOTING
-------------------------------------------------------------------------------

  A device says "In use in exclusive mode"
    Another program has taken it exclusively. Close it, or turn off exclusive
    mode for that device in the Windows sound settings.

  Clicks or dropouts
    Raise the buffer size. 20 ms or 40 ms is fine for monitoring.

  A program does not appear under Applications
    Windows only lists programs that are currently playing. Start playback,
    then reopen the menu.

  A device disappeared and came back
    LoopIt7 reconnects on its own within a few seconds. Nothing to do.

  A program I assigned to a virtual output has gone silent everywhere
    Start routing. A program is only taken off its own output while routing
    is actually running, and it is handed straight back when you stop. If it
    is still silent, switch the toggle in the corner of its box off.

  Windows says the installer might be unsafe
    The download is not code signed. Press More info, then Run anyway.
    Removing that box needs a commercial code signing certificate. Every
    release is built in the open by GitHub Actions from the tagged commit,
    so the build can be checked rather than trusted.


LICENCE
-------------------------------------------------------------------------------

MIT. Built on NAudio by Mark Heath.

===============================================================================

  Built by SeventhSG
  https://github.com/SeventhSG/LoopIt7

===============================================================================
