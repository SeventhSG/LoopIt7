using LoopIt7.Audio;
using LoopIt7.Audio.Graph;
using LoopIt7.ViewModels;

// Checks the two things about routing that are not safe to verify by trying them: the rule
// that refuses a patch which would feed audio back into itself, and the pairing of a cable's
// two ends, which fails silently when it gets the wrong one.
//
// Runs with no window, no settings file and no audio device opened.

int failures = 0;

void Check(string name, bool passed, string detail)
{
    Console.WriteLine($"{(passed ? "  ok  " : "FAILED")}  {name}");
    if (!passed)
    {
        Console.WriteLine($"          {detail}");
        failures++;
    }
}

// One cable's two ends, exactly as Windows reports them: unrelated endpoints that happen to
// share a driver name. Nothing but the family match says they are joined.
var cableIn = new AudioDeviceInfo(
    "{0.0.0.00000000}.{cable-render}", "LoopIt7 Cable", "LoopIt7",
    AudioSourceKind.Render, false);

var cableOut = new AudioDeviceInfo(
    "{0.0.1.00000000}.{cable-capture}", "LoopIt7 Cable", "LoopIt7",
    AudioSourceKind.Capture, false);

var headset = new AudioDeviceInfo(
    "{0.0.0.00000000}.{headset}", "Speakers", "Razer Kraken TE",
    AudioSourceKind.Render, true);

var inputs = new List<AudioDeviceInfo> { cableOut };
var outputs = new List<AudioDeviceInfo> { cableIn, headset };

// The pieces the guard reasons about.
SourceNodeViewModel Pickup() => new(
    "pickup", "LoopIt7 Cable", "LoopIt7", SourceKind.Device, cableOut.Id, 0, string.Empty);

SourceNodeViewModel Tap() => new(
    "tap", "Speakers", "loopback", SourceKind.DeviceLoopback, headset.Id, 0, string.Empty);

bool Guard(
    PatchNodeViewModel from,
    PatchNodeViewModel to,
    List<CableViewModel> cables,
    List<SourceNodeViewModel> sources,
    out string reason) =>
    FeedbackGuard.WouldFeedBack(from, to, cables, sources, inputs, outputs, out reason);

// 1. The case the virtual output binding introduced: a box fed by a cable's recording end,
//    patched back to that same cable's playback end.
{
    var pickup = Pickup();
    var box = new VirtualOutputNodeViewModel("box", "Efe");
    var back = new DestinationNodeViewModel("dest", "LoopIt7 Cable", "LoopIt7", cableIn.Id, true);

    var cables = new List<CableViewModel> { new("c1", pickup, box) };
    bool refused = Guard(box, back, cables, [pickup], out string reason);

    Check("cable pickup patched back to its own playback end is refused", refused, "it was allowed");
    Check("and the reason names both ends",
        refused && reason.Contains("two ends of one cable"), $"reason was: {reason}");
}

// 2. The same cable, but out to a real output. Nothing joins those, so it must be allowed.
{
    var pickup = Pickup();
    var box = new VirtualOutputNodeViewModel("box", "Efe");
    var speakers = new DestinationNodeViewModel("dest", "Speakers", "Razer", headset.Id, false);

    var cables = new List<CableViewModel> { new("c1", pickup, box) };
    bool refused = Guard(box, speakers, cables, [pickup], out string reason);

    Check("the same box out to a real output is allowed", !refused, $"it was refused: {reason}");
}

// 3. The loop does not have to be short. Two virtual outputs in the way must not hide it.
{
    var pickup = Pickup();
    var first = new VirtualOutputNodeViewModel("box1", "First");
    var second = new VirtualOutputNodeViewModel("box2", "Second");
    var back = new DestinationNodeViewModel("dest", "LoopIt7 Cable", "LoopIt7", cableIn.Id, true);

    var cables = new List<CableViewModel> { new("c1", pickup, first), new("c2", first, second) };
    bool refused = Guard(second, back, cables, [pickup], out _);

    Check("a cable loop through two virtual outputs is still refused", refused, "it was allowed");
}

// 4. The rule that was already there: a loopback tap reaching its own device.
{
    var tap = Tap();
    var box = new VirtualOutputNodeViewModel("box", "Efe");
    var sameDevice = new DestinationNodeViewModel("dest", "Speakers", "Razer", headset.Id, false);

    var cables = new List<CableViewModel> { new("c1", tap, box) };
    bool refused = Guard(box, sameDevice, cables, [tap], out _);

    Check("a loopback tap reaching its own device is still refused", refused, "it was allowed");
}

// 5. And the other one: a chain of boxes that closes on itself.
{
    var first = new VirtualOutputNodeViewModel("box1", "First");
    var second = new VirtualOutputNodeViewModel("box2", "Second");

    var cables = new List<CableViewModel> { new("c1", first, second) };
    bool refused = Guard(second, first, cables, [], out _);

    Check("a virtual output chain closing on itself is still refused", refused, "it was allowed");
}

// A driver that offers one recording end for one playback end: the ordinary cable.
{
    var inlets = VirtualCableService.FindInlets(outputs, inputs);

    Check("a plain cable offers exactly one way in", inlets.Count == 1, $"got {inlets.Count}");
    Check("and it names the end other programs choose",
        inlets.Count == 1 && inlets[0].Feed.Id == cableIn.Id, "wrong playback end");
    Check("and the end LoopIt7 opens",
        inlets.Count == 1 && inlets[0].Pickup.Id == cableOut.Id, "wrong recording end");
    Check("real hardware is never offered as a way in",
        inlets.All(i => i.Feed.Id != headset.Id), "a real output was offered");
}

// A driver with several recording ends for one playback end, which is what an Elgato or a
// VoiceMeeter install looks like. Every pairing has to be offered, because guessing one is
// how somebody ends up with a patch that looks right and passes no audio.
{
    var manyOut = new List<AudioDeviceInfo>
    {
        new("{0.0.1.00000000}.{chat}", "Chat Mix", "Elgato Virtual Audio", AudioSourceKind.Capture, false),
        new("{0.0.1.00000000}.{stream}", "Stream Mix", "Elgato Virtual Audio", AudioSourceKind.Capture, false),
        new("{0.0.1.00000000}.{personal}", "Personal Mix", "Elgato Virtual Audio", AudioSourceKind.Capture, false)
    };

    var manyIn = new List<AudioDeviceInfo>
    {
        new("{0.0.0.00000000}.{system}", "System", "Elgato Virtual Audio", AudioSourceKind.Render, false),
        headset
    };

    var inlets = VirtualCableService.FindInlets(manyIn, manyOut);

    Check("a driver with three recording ends offers all three", inlets.Count == 3, $"got {inlets.Count}");
    Check("each one says which end LoopIt7 would listen on",
        inlets.Select(i => i.Detail).Distinct().Count() == 3, "the rows are not distinguishable");
    Check("and they all share the one playback end",
        inlets.All(i => i.Feed.Name == "System"), "playback ends differ");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;
