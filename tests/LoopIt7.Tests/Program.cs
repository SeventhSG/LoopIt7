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

// The name a cable of our own carries before anything on the canvas has a better one. It is
// what a DAW, Discord and OBS all show in their own output list, so it has to be the same
// every time and it has to be unambiguous when there are two.
{
    var settings = new AppSettings();

    Check("a cable of our own is called LoopIt7 Cable",
        CableOwnership.DefaultNameFor(settings) == "LoopIt7 Cable",
        $"got '{CableOwnership.DefaultNameFor(settings)}'");

    settings.ClaimedCables.Add(new CableClaimSettings { ClaimedName = "LoopIt7 Cable" });

    Check("a second one is told apart from the first",
        CableOwnership.DefaultNameFor(settings) == "LoopIt7 Cable 2",
        $"got '{CableOwnership.DefaultNameFor(settings)}'");

    var (feed, pickup) = CableOwnership.NamesFor(CableOwnership.DefaultName, "VB-Audio");

    Check("and that is what the other program's list reads",
        feed.Display == "LoopIt7 Cable (LoopIt7 · VB-Audio)", $"got '{feed.Display}'");
    Check("with the end we listen on told apart from it",
        pickup.Display == "LoopIt7 Cable pickup (LoopIt7 · VB-Audio)", $"got '{pickup.Display}'");
}

// Naming a cable without being asked, and stopping. The first is what makes the way in
// findable at all; the second is the promise that "give the name back" means permanently.
{
    var settings = new AppSettings();
    CableOwnership.ObserveCables(settings, [cableIn, cableOut], "LoopIt7");

    Check("a cable setup installed is named without waiting to be asked",
        CableOwnership.MayNameUnasked(settings, cableIn), "it would have been left alone");

    var claim = new CableClaimSettings
    {
        RenderEndpointId = cableIn.Id,
        CaptureEndpointId = cableOut.Id,
        ClaimedName = "LoopIt7 Cable",
        OriginalRenderName = "CABLE Input",
        OriginalRenderInterface = "VB-Audio Virtual Cable",
        OriginalCaptureName = "CABLE Output",
        OriginalCaptureInterface = "VB-Audio Virtual Cable"
    };

    settings.ClaimedCables.Add(claim);
    CableOwnership.Disown(settings, claim, [], out _);

    Check("giving the name back drops the claim",
        settings.ClaimedCables.Count == 0, $"{settings.ClaimedCables.Count} left");

    Check("and nothing names it again on the next look",
        !CableOwnership.MayNameUnasked(settings, cableIn), "it would have been renamed again");

    Check("though the cable is still one LoopIt7 could name if asked",
        CableOwnership.MayClaim(settings, cableIn), "ownership was thrown away with the name");
}

// One vendor, several products, and only one of them is a cable. Told apart on the driver
// string, most specific first, because "VB-Audio" alone covers three unrelated things and
// pairing a VB-CABLE playback end with a VoiceMeeter recording end fails without a sound.
{
    var vbCableIn = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vb-in}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    var vbCableOut = new AudioDeviceInfo(
        "{0.0.1.00000000}.{vb-out}", "CABLE Output", "VB-Audio Virtual Cable",
        AudioSourceKind.Capture, false);

    var vaioIn = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vaio-in}", "VoiceMeeter Input", "VB-Audio VoiceMeeter VAIO",
        AudioSourceKind.Render, false);

    var vaioOut = new AudioDeviceInfo(
        "{0.0.1.00000000}.{vaio-out}", "VoiceMeeter Output", "VB-Audio VoiceMeeter VAIO",
        AudioSourceKind.Capture, false);

    var waveLink = new AudioDeviceInfo(
        "{0.0.0.00000000}.{wave}", "System", "Elgato Virtual Audio",
        AudioSourceKind.Render, false);

    Check("VoiceMeeter is not filed under the plain cable",
        VirtualCableService.FamilyOf(vaioIn) == "VoiceMeeter",
        $"got '{VirtualCableService.FamilyOf(vaioIn)}'");

    var pickups = VirtualCableService.FindPickupEndpoints(vbCableIn, [vbCableOut, vaioOut]);

    Check("so a cable's recording end is its own and not VoiceMeeter's",
        pickups.Count == 1 && pickups[0].Id == vbCableOut.Id,
        $"paired with {string.Join(", ", pickups.Select(p => p.Name))}");

    Check("a cable is a cable",
        VirtualCableService.IsPlainCable(vbCableIn), "the plain cable was not recognised");

    Check("a program's own virtual device is not one to rename",
        !VirtualCableService.IsPlainCable(waveLink) && !VirtualCableService.IsPlainCable(vaioIn),
        "somebody else's product was offered up for renaming");
}

// A VB-Audio cable, the way in from a DAW, and which end is which. Worth its own check
// because the vendor's two names read backwards from ours: "CABLE Input" is a playback
// endpoint, the thing a DAW picks as its output, and "CABLE Output" is the recording endpoint
// LoopIt7 opens to hear it. Getting these the wrong way round leaves a patch that looks right
// with meters that never move.
{
    var vbInput = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vb-input}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    var vbOutput = new AudioDeviceInfo(
        "{0.0.1.00000000}.{vb-output}", "CABLE Output", "VB-Audio Virtual Cable",
        AudioSourceKind.Capture, false);

    var inlets = VirtualCableService.FindInlets([vbInput], [vbOutput]);

    Check("a VB-Audio cable offers one way in", inlets.Count == 1, $"got {inlets.Count}");

    var inlet = inlets[0];

    Check("the DAW is pointed at CABLE Input, which is a playback endpoint",
        inlet.Feed.Id == vbInput.Id && inlet.Feed.Kind == AudioSourceKind.Render,
        $"the DAW was sent to {inlet.Feed.Name}");

    Check("and the source box opens CABLE Output, the recording end",
        inlet.Pickup.Id == vbOutput.Id && inlet.Pickup.Kind == AudioSourceKind.Capture,
        $"LoopIt7 would open {inlet.Pickup.Name}");

    // What a source box added from that inlet is called: the end the DAW picks, because that
    // name is the only part of this the user sees anywhere outside LoopIt7.
    Check("the box on the canvas reads as the end the DAW picks",
        inlet.Title == "CABLE Input", $"got '{inlet.Title}'");

    var (feed, pickup) = CableOwnership.NamesFor(
        CableOwnership.DefaultName, VirtualCableService.VendorOf(vbInput));

    Check("once it is ours, that is what the DAW's output list says",
        feed.Display == "LoopIt7 Cable (LoopIt7 · VB-Audio)", $"got '{feed.Display}'");

    Check("and the end we open is named apart from it",
        pickup.Display == "LoopIt7 Cable pickup (LoopIt7 · VB-Audio)", $"got '{pickup.Display}'");
}

// Handing over a cable that was here first. Nothing automatic may do this, which is why the
// door only opens from the user's side of it.
{
    var theirs = new AudioDeviceInfo(
        "{0.0.0.00000000}.{theirs-render}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    var theirsOut = new AudioDeviceInfo(
        "{0.0.1.00000000}.{theirs-capture}", "CABLE Output", "VB-Audio Virtual Cable",
        AudioSourceKind.Capture, false);

    var settings = new AppSettings();
    CableOwnership.ObserveCables(settings, [theirs, theirsOut], null);

    Check("a cable that was here first is still left alone by itself",
        !CableOwnership.MayNameUnasked(settings, theirs), "it was renamed unasked");

    CableOwnership.Adopt(settings, theirs, theirsOut);

    Check("handing it over makes both ends ours to name",
        CableOwnership.MayClaim(settings, theirs) && CableOwnership.MayClaim(settings, theirsOut),
        "one end stayed off limits");

    Check("and it stops counting as somebody else's",
        settings.ForeignCableIds.Count == 0, $"{settings.ForeignCableIds.Count} still foreign");
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

// The list the uninstaller actually passes: outputs plus input sources, where every playback
// endpoint turns up twice because its loopback tap carries the same id. That once threw inside
// the release, the uninstaller swallowed it, and every name stayed on.
{
    var settings = new AppSettings();
    settings.ClaimedCables.Add(new CableClaimSettings
    {
        RenderEndpointId = cableIn.Id,
        CaptureEndpointId = cableOut.Id,
        ClaimedName = "LoopIt7 Cable",
        OriginalRenderName = "Line 1",
        OriginalRenderInterface = "Virtual Audio Cable",
        OriginalCaptureName = "Line 1",
        OriginalCaptureInterface = "Virtual Audio Cable"
    });

    var tapOfCable = new AudioDeviceInfo(cableIn.Id, cableIn.Name, cableIn.InterfaceName, AudioSourceKind.Loopback, false);
    var tapOfHeadset = new AudioDeviceInfo(headset.Id, headset.Name, headset.InterfaceName, AudioSourceKind.Loopback, true);

    string? error = null;
    try
    {
        CableOwnership.ReleaseAll(settings, [cableIn, headset, cableOut, tapOfCable, tapOfHeadset]);
    }
    catch (Exception ex)
    {
        error = ex.Message;
    }

    Check("releasing with loopback taps in the list does not throw", error is null, error ?? string.Empty);
    Check("and the claim is dropped", settings.ClaimedCables.Count == 0, $"{settings.ClaimedCables.Count} left");
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

// A cable the LoopIt7 installer put there. Setup leaves a note saying so, because by the
// time the app first runs it is indistinguishable from one the user has had for years, and
// without the note we would refuse to rename the very cable we installed for them.
{
    var installed = new AudioDeviceInfo(
        "{0.0.0.00000000}.{fresh}", "CABLE Input", "VB-Audio Virtual Cable",
        AudioSourceKind.Render, false);

    var theirElgato = new AudioDeviceInfo(
        "{0.0.0.00000000}.{elgato}", "System", "Elgato Virtual Audio",
        AudioSourceKind.Render, false);

    // One vendor, three products. Installing the plain cable is no licence to rename the
    // other two, which is why ownership is matched on the driver string and not the vendor.
    var theirVoiceMeeter = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vaio}", "VoiceMeeter Input", "VB-Audio VoiceMeeter VAIO",
        AudioSourceKind.Render, false);

    var theirCableA = new AudioDeviceInfo(
        "{0.0.0.00000000}.{cablea}", "CABLE-A Input", "VB-Audio Cable A",
        AudioSourceKind.Render, false);

    var settings = new AppSettings();
    CableOwnership.ObserveCables(
        settings,
        [installed, theirElgato, theirVoiceMeeter, theirCableA],
        "VB-Audio Virtual Cable");

    Check("a cable setup installed is ours from the first look",
        CableOwnership.MayClaim(settings, installed), "it was not claimable");

    Check("somebody else's Elgato still is not",
        !CableOwnership.MayClaim(settings, theirElgato), "the Elgato became claimable");

    Check("nor is VoiceMeeter, same vendor though it is",
        !CableOwnership.MayClaim(settings, theirVoiceMeeter), "VoiceMeeter became claimable");

    Check("nor their other VB-Audio cable",
        !CableOwnership.MayClaim(settings, theirCableA), "Cable A became claimable");

    // Without the note, the same machine reads completely differently.
    var blind = new AppSettings();
    CableOwnership.ObserveCables(blind, [installed, theirElgato], null);

    Check("without the note the same cable would be left alone",
        !CableOwnership.MayClaim(blind, installed), "it was claimable with no note");

    // The A+B pack is two drivers, and setup notes both. Each is matched in full, so a C+D
    // pack on the same machine, same vendor and same wording up to the letter, stays theirs.
    var packB = new AudioDeviceInfo(
        "{0.0.0.00000000}.{cableb}", "CABLE-B Input", "VB-Audio Cable B",
        AudioSourceKind.Render, false);
    var theirCableC = new AudioDeviceInfo(
        "{0.0.0.00000000}.{cablec}", "CABLE-C Input", "VB-Audio Cable C",
        AudioSourceKind.Render, false);

    var pack = new AppSettings();
    CableOwnership.ObserveCables(pack, [theirCableA, packB, theirCableC, installed], "VB-Audio Cable A|VB-Audio Cable B");

    Check("both cables of the A+B pack setup installed are ours",
        CableOwnership.MayClaim(pack, theirCableA) && CableOwnership.MayClaim(pack, packB), "A or B was not claimable");

    Check("a C+D pack beside it is not",
        !CableOwnership.MayClaim(pack, theirCableC), "Cable C became claimable");

    Check("nor the plain cable",
        !CableOwnership.MayClaim(pack, installed), "the plain cable became claimable");
}

// Add source and Add output list physical devices only. VoiceMeeter and VB-CABLE name their ends
// from their own side, "Input" for the end you play into, which is a playback device, and listed
// among the outputs it read as the wrong way round. Every virtual device goes through Add cable.
{
    var vmInput = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vm-input}", "Voicemeeter Input", "VB-Audio Voicemeeter VAIO",
        AudioSourceKind.Render, false);
    var vmOutB1 = new AudioDeviceInfo(
        "{0.0.1.00000000}.{vm-b1}", "Voicemeeter Out B1", "VB-Audio Voicemeeter VAIO",
        AudioSourceKind.Capture, false);
    var waveLinkSystem = new AudioDeviceInfo(
        "{0.0.0.00000000}.{wl-system}", "System", "Elgato Virtual Audio", AudioSourceKind.Loopback, false);
    var speakersTap = new AudioDeviceInfo(
        "{0.0.0.00000000}.{speakers}", "Speakers", "Realtek(R) Audio", AudioSourceKind.Loopback, true);
    var headsetMic = new AudioDeviceInfo(
        "{0.0.1.00000000}.{headset-mic}", "Headset Microphone", "Razer Kraken TE", AudioSourceKind.Capture, false);

    Check("VoiceMeeter's Input is kept out of Add output",
        !VirtualCableService.IsPhysical(vmInput), "it was listed as physical");
    Check("its Out B1 is kept out of Add source",
        !VirtualCableService.IsPhysical(vmOutB1), "it was listed as physical");
    Check("and so is a loopback tap on Wave Link",
        !VirtualCableService.IsPhysical(waveLinkSystem), "it was listed as physical");
    Check("a cable of our own is kept out too",
        !VirtualCableService.IsPhysical(cableIn) && !VirtualCableService.IsPhysical(cableOut), "it was listed as physical");
    Check("a real microphone, speaker and speaker tap stay in",
        VirtualCableService.IsPhysical(headsetMic) && VirtualCableService.IsPhysical(headset) &&
        VirtualCableService.IsPhysical(speakersTap), "one was taken for virtual");

    Check("a loopback tap says what it records",
        speakersTap.MenuSubtitle == "what this device is playing", $"got '{speakersTap.MenuSubtitle}'");
    Check("a real microphone keeps its plain line",
        headsetMic.MenuSubtitle == "Razer Kraken TE", $"got '{headsetMic.MenuSubtitle}'");

    var vbInput = new AudioDeviceInfo(
        "{0.0.0.00000000}.{vb-input}", "CABLE Input", "VB-Audio Virtual Cable", AudioSourceKind.Render, false);
    var vbOutput = new AudioDeviceInfo(
        "{0.0.1.00000000}.{vb-output}", "CABLE Output", "VB-Audio Virtual Cable", AudioSourceKind.Capture, false);
    var vbInlet = VirtualCableService.FindInlets([vbInput], [vbOutput]).Single();

    // Add cable offers each end once, the way it is named: Input as a way in, Output as a way out.
    Check("the In row is the end a program plays into",
        vbInlet.Title == "CABLE Input", $"got '{vbInlet.Title}'");
    Check("the Out row is the end a program records from",
        vbInlet.OutTitle == "CABLE Output", $"got '{vbInlet.OutTitle}'");
}

// A device Windows will not open right now still shows in the menus, and says why, so a sound
// card with nothing plugged in reads as unplugged rather than missing.
{
    var realtekSpeakers = new AudioDeviceInfo(
        "{0.0.0.00000000}.{realtek}", "Speakers", "Realtek(R) Audio", AudioSourceKind.Render, false,
        Unavailable: "nothing plugged in");

    // Named with its card too: a PC often has two devices called Speakers, one of them live.
    Check("an unplugged sound card says which card and why",
        realtekSpeakers.MenuSubtitle == "Realtek(R) Audio · nothing plugged in", $"got '{realtekSpeakers.MenuSubtitle}'");
    Check("and still counts as a real device",
        VirtualCableService.IsPhysical(realtekSpeakers), "it was taken for virtual");
}

// A cable's queue, driven the way a real one is. Packet sizes and spacing as measured on a real
// machine: 10 ms packets 6 to 14 ms apart from an ordinary device, 3 ms packets 3 to 5 ms apart
// from a virtual cable in low latency mode, and an output pulling 10 ms every 10 ms on its own
// clock, which is all most hardware offers. Any sample that comes out as silence after the
// start is a gap the listener hears as crackle.
{
    var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    (double Trimmed, int Underruns, int GapSamples, int Corrections, double Queue) Run(
        double writerClock, int seconds, double packetMs, double jitterMs, double pullMs = 10)
    {
        var cable = new Connection("c", "s", "d", format);
        double now = 0;
        cable.Clock = () => now;
        var tail = cable.CreateTail(48000);
        var rng = new Random(7);

        int packetFrames = (int)(48 * packetMs);
        var packet = new byte[packetFrames * 2 * 4];
        for (int i = 0; i < packetFrames * 2; i++) BitConverter.TryWriteBytes(packet.AsSpan(i * 4), 0.25f);

        var block = new float[(int)(96 * pullMs)];
        double interval = packetMs / writerClock;
        double nextWrite = rng.NextDouble() * jitterMs;
        double nextRead = 5.0;
        int written = 0, gaps = 0;
        bool started = false;

        while (nextRead < seconds * 1000.0)
        {
            if (nextWrite <= nextRead)
            {
                now = nextWrite;
                cable.Write(packet, packet.Length, 3);
                written++;
                nextWrite = written * interval + rng.NextDouble() * jitterMs;
            }
            else
            {
                now = nextRead;
                tail.Read(block, 0, block.Length);
                if (!started && block[0] != 0f) started = true;
                if (started) gaps += block.Count(s => s == 0f);
                nextRead += pullMs;
            }
        }

        return (cable.TrimmedMilliseconds, cable.Underruns, gaps, cable.DriftCorrections, cable.SettledQueueMilliseconds);
    }

    // The last two are what low latency mode runs on a USB headset: 144 sample periods at
    // both ends, and a shared 10 ms microphone feeding a 3 ms output. Both ran dry on a real
    // machine under the first version of the reader.
    foreach (var (name, packetMs, jitterMs, pullMs) in new[]
    {
        ("10 ms device", 10.0, 4.0, 10.0),
        ("3 ms cable", 3.0, 2.0, 10.0),
        ("3 ms in, 3 ms out", 3.0, 1.0, 3.0),
        ("10 ms mic, 3 ms out", 10.0, 1.0, 3.0)
    })
    {
        var even = Run(1.0, 60, packetMs, jitterMs, pullMs);
        Check($"{name}: jitter alone leaves no gaps and throws nothing away",
            even.GapSamples == 0 && even.Underruns == 0 && even.Trimmed == 0,
            $"{even.GapSamples} silent samples, {even.Underruns} underruns, {even.Trimmed:0} ms trimmed");

        // What waits in the queue after each pull: one packet and a margin, not a safety buffer.
        Check($"{name}: the queue settles at one packet and a margin",
            even.Queue <= packetMs + 3, $"settled at {even.Queue:0.0} ms");

        foreach (double drift in new[] { 1.002, 0.998 })
        {
            var run = Run(drift, 60, packetMs, jitterMs, pullMs);
            Check($"{name}: a clock {(drift > 1 ? "fast" : "slow")} by 0.2% is followed without a gap",
                run.GapSamples == 0 && run.Underruns == 0 && run.Trimmed == 0 && run.Corrections > 0,
                $"{run.GapSamples} silent samples, {run.Underruns} underruns, {run.Trimmed:0} ms trimmed, {run.Corrections} corrections, settled {run.Queue:0.0} ms");
        }

        // A device running at the wrong speed altogether: a Behringer under VoiceMeeter took 8%
        // less than it was sent on a real machine, and nudging a sample at a time threw away 24
        // seconds of audio. The resampler has to follow it, allowing at most one catch up while
        // it learns the speed.
        foreach (double wrong in new[] { 1.08, 0.92 })
        {
            var run = Run(wrong, 60, packetMs, jitterMs, pullMs);
            Check($"{name}: a device {(wrong > 1 ? "8% slow" : "8% fast")} is followed without cutting audio",
                run.Trimmed < 150 && run.Underruns <= 1,
                $"{run.Trimmed:0} ms trimmed, {run.Underruns} underruns, {run.GapSamples} silent samples, settled {run.Queue:0.0} ms");
        }
    }
}

// The runaway guard. A loop through another program is dipped, like pulling a fader down and
// up, and muted only when it keeps coming back. Music, however loud, is left alone.
{
    const int rate = 48000;

    (RunawayGuardSampleProvider Guard, int Trips, float LastSecondPeak) Listen(Func<double, double, float> signal, int seconds)
    {
        var noise = new Random(3);
        var guard = new RunawayGuardSampleProvider(new FuncSource(i => signal(i / 2 / (double)rate, noise.NextDouble() * 2 - 1), rate));
        int trips = 0;
        guard.Runaway += (_, _) => trips++;

        var block = new float[960];
        float lastPeak = 0f;
        for (int b = 0; b < seconds * 100; b++)
        {
            guard.Read(block, 0, block.Length);
            if (b == (seconds - 1) * 100) lastPeak = 0f;
            foreach (float s in block) lastPeak = Math.Max(lastPeak, Math.Abs(s));
        }

        return (guard, trips, lastPeak);
    }

    // Fizz round a loop at unity gain: every pass adds to what is already going round, so it
    // climbs steadily until it hits the ceiling, and comes straight back after every dip.
    var loop = Listen((t, n) => (float)(n * Math.Min(3.0, 0.005 * Math.Pow(2.5, t))), 10);
    Check("a loop that keeps building is dipped first", loop.Guard.Dips >= 1, $"{loop.Guard.Dips} dips");
    Check("then muted when it keeps coming back", loop.Guard.Tripped && loop.Trips == 1, $"tripped {loop.Guard.Tripped}, {loop.Trips} trips");
    Check("and stays silent", loop.LastSecondPeak == 0f, $"still peaking at {loop.LastSecondPeak:0.000}");

    loop.Guard.Rearm();
    Check("unmuting lets sound through again", !loop.Guard.Tripped, "still tripped after rearming");

    // The same climb, but whatever fed it stopped: one dip clears it and the sound carries on.
    var cleared = Listen((t, n) => (float)(n * Math.Min(0.5, 0.005 * Math.Pow(2.5, t))), 8);
    Check("a build-up that stops is dipped and then plays on",
        cleared.Guard.Dips >= 1 && !cleared.Guard.Tripped && cleared.LastSecondPeak > 0.3f,
        $"{cleared.Guard.Dips} dips, tripped {cleared.Guard.Tripped}, last second peaks at {cleared.LastSecondPeak:0.00}");

    var tone = Listen((t, _) => (float)(0.9 * Math.Sin(2 * Math.PI * 440 * t)), 10);
    Check("a loud tone just under full scale plays on", tone.Guard.Dips == 0 && !tone.Guard.Tripped, $"{tone.Guard.Dips} dips");

    var master = Listen((t, _) => (float)(0.97 * Math.Sign(Math.Sin(2 * Math.PI * 110 * t))), 10);
    Check("a brickwalled master peaking at -0.3 dB plays on", master.Guard.Dips == 0 && !master.Guard.Tripped, $"{master.Guard.Dips} dips");

    var swell = Listen((t, _) => (float)(Math.Min(0.9, 0.05 + 0.85 * t / 4) * Math.Sin(2 * Math.PI * 330 * t)), 6);
    Check("a four second swell to full volume plays on", swell.Guard.Dips == 0 && !swell.Guard.Tripped, $"{swell.Guard.Dips} dips");

    // A drum pattern getting louder: it climbs, but falls back between hits.
    var drums = Listen((t, n) => (float)(n * Math.Min(0.9, 0.1 + 0.4 * t) * Math.Exp(-(t % 0.25) * 20)), 6);
    Check("drums getting louder play on", drums.Guard.Dips == 0 && !drums.Guard.Tripped, $"{drums.Guard.Dips} dips");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;

/// <summary>Interleaved stereo float from a function of the interleaved sample index.</summary>
sealed class FuncSource(Func<int, float> signal, int sampleRate) : NAudio.Wave.ISampleProvider
{
    private int _index;

    public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++) buffer[offset + i] = signal(_index++);
        return count;
    }
}
