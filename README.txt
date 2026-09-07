===============================================================================

  L O O P I T 7
  A patchbay for Windows audio. Every source, every output, and the cables
  in between.

  Version 1.0.0
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


GETTING STARTED
-------------------------------------------------------------------------------

  1. Open LoopIt7.
  2. Press "Add source" and pick a microphone, a playback device to tap, or a
     running program.
  3. Press "Add output" and pick where it should land.
  4. Drag from the circle on the right of the source to the output box.
  5. Press "Start routing".

Drag the boxes anywhere you like. The layout is saved with your setup.


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

So LoopIt7 does not pretend. Everything above works with no driver installed.

The one thing a cable is genuinely needed for is handing a mix back to another
program as if it were a microphone. For that, LoopIt7 uses whatever cable is
already on your machine. It recognises VB-Audio Cable, VoiceMeeter, Elgato
Virtual Audio and NVIDIA Broadcast, and points you at VB-Audio's free cable if
there is none:

    https://vb-audio.com/Cable/


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


LICENCE
-------------------------------------------------------------------------------

MIT. Built on NAudio by Mark Heath.

===============================================================================

  Built by SeventhSG
  https://github.com/SeventhSG/LoopIt7

===============================================================================
