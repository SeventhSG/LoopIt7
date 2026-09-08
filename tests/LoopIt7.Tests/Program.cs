using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using LoopIt7.Audio;
using LoopIt7.Audio.Graph;
using LoopIt7.Models;
using LoopIt7.ViewModels;

// Checks the three things that are not safe to verify by trying them: the rule that refuses
// a patch which would feed audio back into itself, the pairing of a cable's two ends, which
// fails silently when it gets the wrong one, and which cables LoopIt7 is allowed to rename,
// which is the one that reaches outside the app and touches somebody else's setup.
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

// Which cables LoopIt7 may rename. Getting this wrong renames a device that somebody else's
// OBS scene or Discord setting points at, on a machine we do not get to see.
{
    var theirs = new AudioDeviceInfo(
        "{0.0.0.00000000}.{theirs}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    var settings = new AppSettings();

    // First run: whatever is here belongs to whoever put it here.
    CableOwnership.ObserveCables(settings, [theirs]);

    Check("the first look records what was already here", settings.CableBaselineTaken, "no baseline taken");

    // A playback endpoint arrives twice, once as itself and once as the loopback source that
    // taps it, both under the same id. The baseline must not grow a copy for each.
    var withLoopback = new AppSettings();
    var sameEndpointAsLoopback = theirs with { Kind = AudioSourceKind.Loopback };
    CableOwnership.ObserveCables(withLoopback, [theirs, sameEndpointAsLoopback]);

    Check("an endpoint seen twice is recorded once",
        withLoopback.ForeignCableIds.Count == 1, $"recorded {withLoopback.ForeignCableIds.Count}");
    Check("a cable that was here first may not be renamed",
        !CableOwnership.MayClaim(settings, theirs), "it was claimable");

    // A cable turning up on its own, with nobody having asked, is somebody else's business too.
    CableOwnership.ObserveCables(settings, [theirs, cableIn]);

    Check("a cable that appears unasked may not be renamed",
        !CableOwnership.MayClaim(settings, cableIn), "it was claimable");

    // Now the user asks LoopIt7 for one, and one arrives.
    settings.AwaitingCable = true;
    CableOwnership.ObserveCables(settings, [theirs, cableIn, cableOut]);

    Check("a cable that arrives after LoopIt7 asked may be renamed",
        CableOwnership.MayClaim(settings, cableOut), "it was not claimable");
    Check("and asking is a one time thing", !settings.AwaitingCable, "the flag is still set");
    Check("the pre-existing cable is still off limits",
        !CableOwnership.MayClaim(settings, theirs), "it became claimable");

    // Claiming somebody else's cable has to be refused outright, not merely not offered.
    bool claimed = CableOwnership.TryClaim(settings, theirs, cableOut, "Efe", out string? refusal);
    Check("claiming a pre-existing cable is refused", !claimed, "it was allowed");
    Check("and says why", refusal is not null && refusal.Contains("already on this machine"),
        $"reason was: {refusal}");
    Check("a refused claim records nothing", settings.ClaimedCables.Count == 0,
        $"{settings.ClaimedCables.Count} claim(s) recorded");
}

// Handing a cable back. The bookkeeping has to hold even when the cable itself has gone,
// which is exactly the state an uninstall on a changed machine runs in.
{
    var settings = new AppSettings();
    settings.ClaimedCables.Add(new CableClaimSettings
    {
        RenderEndpointId = cableIn.Id,
        CaptureEndpointId = cableOut.Id,
        ClaimedName = "Efe",
        OriginalRenderName = "CABLE Input",
        OriginalRenderInterface = "VB-Audio Virtual Cable",
        OriginalCaptureName = "CABLE Output",
        OriginalCaptureInterface = "VB-Audio Virtual Cable"
    });

    // The cable was uninstalled before LoopIt7 was: there is nothing left to rename.
    CableOwnership.ReleaseAll(settings, []);

    Check("releasing a cable that is gone still drops the claim",
        settings.ClaimedCables.Count == 0, $"{settings.ClaimedCables.Count} left");
}

// The empty state somebody with no cable sees. Worth its own check because at any non zero
// count a binding that does not resolve looks exactly like one that does: both leave the
// panel collapsed. Only the zero case can tell them apart.
{
    Visibility atZero = Visibility.Collapsed;
    Visibility atThree = Visibility.Visible;

    const string Markup = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel.Style>
            <Style TargetType="StackPanel">
              <Setter Property="Visibility" Value="Collapsed" />
              <Style.Triggers>
                <DataTrigger Binding="{Binding Inlets.Count}" Value="0">
                  <Setter Property="Visibility" Value="Visible" />
                </DataTrigger>
              </Style.Triggers>
            </Style>
          </StackPanel.Style>
          <TextBlock Text="no cable here" />
        </StackPanel>
        """;

    Visibility MeasureAt(int inletCount)
    {
        var box = new VirtualOutputNodeViewModel("box", "Efe") { Inlets = [] };

        for (int i = 0; i < inletCount; i++)
        {
            box.Inlets!.Add(new CableInlet(
                new AudioDeviceInfo($"feed{i}", "System", "Elgato", AudioSourceKind.Render, false),
                new AudioDeviceInfo($"pick{i}", "Chat Mix", "Elgato", AudioSourceKind.Capture, false)));
        }

        var panel = (StackPanel)XamlReader.Parse(Markup);
        var host = new Border { Child = panel, DataContext = box };

        host.Measure(new Size(400, 400));
        host.Arrange(new Rect(0, 0, 400, 400));
        host.UpdateLayout();

        return panel.Visibility;
    }

    // WPF needs a single threaded apartment, which a console app does not start in.
    var ui = new Thread(() =>
    {
        atZero = MeasureAt(0);
        atThree = MeasureAt(3);
    });

    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();

    Check("with no cable on the machine the picker explains itself",
        atZero == Visibility.Visible, $"the empty state was {atZero}");
    Check("and gets out of the way once there is one",
        atThree == Visibility.Collapsed, $"the empty state was {atThree}");
}

// What a claimed cable ends up called, in every program's device list on the machine.
{
    var vb = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vb}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    Check("the driver's maker is recognised", VirtualCableService.VendorOf(vb) == "VB-Audio",
        $"got '{VirtualCableService.VendorOf(vb)}'");

    var (feed, pickup) = CableOwnership.NamesFor("Efe", VirtualCableService.VendorOf(vb));

    Check("the end other programs pick reads as the box",
        feed.Display == "Efe (LoopIt7 · VB-Audio)", $"got '{feed.Display}'");

    Check("the end LoopIt7 taps is told apart from it",
        pickup.Display == "Efe pickup (LoopIt7 · VB-Audio)", $"got '{pickup.Display}'");

    Check("so nobody can pick the wrong one by name", feed.Display != pickup.Display,
        "both ends are called the same thing");

    Check("whoever made the driver stays visible",
        feed.Display.Contains("VB-Audio") && pickup.Display.Contains("VB-Audio"),
        "the vendor was dropped from the name");

    // An unrecognised cable has no vendor to credit, and an empty bracket would read badly.
    var (plainFeed, _) = CableOwnership.NamesFor("Efe", string.Empty);
    Check("an unknown driver leaves no empty brackets behind",
        plainFeed.Display == "Efe (LoopIt7)", $"got '{plainFeed.Display}'");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;
