using LoopIt7.Models;
using System.IO;

namespace LoopIt7.Audio;

/// <summary>
/// Tells apart the two kinds of virtual cable on a machine, which is the whole difference
/// between a feature people like and one that makes them uninstall.
/// <para>
/// A <b>foreign</b> cable was already there: VoiceMeeter's VAIO, an Elgato, a VB-Audio cable
/// somebody installed years ago. LoopIt7 will happily route to it, but never renames it. Those
/// names are written down in other people's OBS scenes, Discord settings and VoiceMeeter
/// configs, and changing one silently breaks a setup we did not build.
/// </para>
/// <para>
/// An <b>owned</b> cable is one LoopIt7 installed. That one gets a LoopIt7 name through
/// <see cref="EndpointNaming"/>, so it reads the same in every program's device list, and the
/// names it had before are kept so releasing the claim puts them back.
/// </para>
/// <para>
/// Ownership is per cable, not per driver, which is what lets LoopIt7 and VoiceMeeter share a
/// machine without argument. VoiceMeeter's virtual devices come from its own VAIO driver, not
/// from VB-CABLE, so the two never contend for the same endpoint. Where they do overlap, when
/// somebody has pointed a VoiceMeeter hardware out at the same cable, WASAPI shared mode simply
/// sums them, and the exclusive mode case already surfaces as a status on the destination.
/// </para>
/// </summary>
public static class CableOwnership
{
    /// <summary>The bracketed half every cable LoopIt7 owns carries.</summary>
    public const string OwnedInterfaceName = "LoopIt7";

    /// <summary>
    /// What to call the two ends of a cable LoopIt7 has taken over.
    /// <para>
    /// They get different names on purpose. The playback end is the one somebody picks in
    /// Discord, so it reads as the box itself. The recording end is LoopIt7's own tap on the
    /// same cable, and it appears in every microphone list on the machine: naming it the same
    /// as the playback end would invite somebody to pick it in OBS expecting the mix and get
    /// whatever was sent in instead.
    /// </para>
    /// <para>
    /// The bracket keeps the driver's maker visible. The cable is somebody else's work, and
    /// after a rename nothing else on the machine would say so.
    /// </para>
    /// </summary>
    public static (EndpointNames Feed, EndpointNames Pickup) NamesFor(string boxTitle, string vendor)
    {
        string title = boxTitle.Trim();

        string bracket = vendor.Length == 0
            ? OwnedInterfaceName
            : $"{OwnedInterfaceName} · {vendor}";

        return (new EndpointNames(title, bracket), new EndpointNames($"{title} pickup", bracket));
    }

    /// <summary>
    /// Notes which cables were already here, once, on the first run that ever looks. Everything
    /// present at that moment belongs to somebody else's setup and is off limits for renaming
    /// for the rest of the install.
    /// <para>
    /// After that, a cable becomes LoopIt7's only if it turns up while the user is away getting
    /// one at LoopIt7's asking. That causal link is the whole test: without it, a VoiceMeeter
    /// installed next month would be indistinguishable from a cable we put there, and we would
    /// rename somebody's devices out from under them.
    /// </para>
    /// </summary>
    /// <returns>True when something was recorded, so the caller knows to save.</returns>
    public static bool ObserveCables(AppSettings settings, IEnumerable<AudioDeviceInfo> devices)
    {
        // A playback endpoint turns up twice, once as itself and once as the loopback source
        // that taps it, and both carry the same endpoint id. Provenance is about endpoints,
        // not about the ways we can open them.
        var present = devices
            .Where(VirtualCableService.IsVirtual)
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        string? installed = InstalledCableFamily();

        bool Ours(AudioDeviceInfo device) =>
            installed is not null &&
            string.Equals(VirtualCableService.FamilyOf(device), installed, StringComparison.OrdinalIgnoreCase);

        if (!settings.CableBaselineTaken)
        {
            // A cable the LoopIt7 installer put here is ours from the first moment we look.
            // Without this it would be indistinguishable from one the user had all along, and
            // we would refuse to rename the very cable we installed for them.
            settings.OwnCableIds = present.Where(Ours).Select(d => d.Id).ToList();
            settings.ForeignCableIds = present.Where(d => !Ours(d)).Select(d => d.Id).ToList();
            settings.CableBaselineTaken = true;
            return true;
        }

        var arrived = present
            .Where(d => !settings.ForeignCableIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .Where(d => !settings.OwnCableIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (arrived.Count == 0) return false;

        // Two ways a new cable becomes ours: our own installer put it there, or the user went
        // and fetched one because LoopIt7 asked them to. Anything else that turns up is
        // somebody else's business, whatever it is.
        var claimable = arrived.Where(d => Ours(d) || settings.AwaitingCable).ToList();

        if (claimable.Count == 0) return false;

        settings.OwnCableIds.AddRange(claimable.Select(d => d.Id));
        if (settings.AwaitingCable) settings.AwaitingCable = false;

        return true;
    }

    /// <summary>
    /// The cable family the LoopIt7 installer put on this machine, or null when it installed
    /// none because one was already here.
    /// <para>
    /// Setup writes this, because setup is the only thing that knows. By the time the app
    /// runs, a cable it installed thirty seconds ago and a cable the user has had for two
    /// years look exactly alike.
    /// </para>
    /// </summary>
    public static string? InstalledCableFamily()
    {
        // Setup installs per user by default and only asks for administrator rights when
        // somebody chooses an all users install, so the note lands in whichever of the two
        // it could actually write to.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.CommonApplicationData
                 })
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(folder), "LoopIt7", "installed-cable.txt");

                if (!File.Exists(path)) continue;

                string family = File.ReadAllText(path).Trim();
                if (family.Length > 0) return family;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is the same as absent: we simply do not claim anything.
            }
        }

        return null;
    }

    /// <summary>
    /// Whether LoopIt7 is allowed to rename this endpoint. True only for a cable that arrived
    /// because LoopIt7 asked for one. Everything else, including every cable that was here
    /// first, keeps the name it came with.
    /// </summary>
    public static bool MayClaim(AppSettings settings, AudioDeviceInfo device) =>
        VirtualCableService.IsVirtual(device) &&
        settings.OwnCableIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this endpoint is one LoopIt7 installed and renamed.</summary>
    public static bool IsOwned(AppSettings settings, AudioDeviceInfo device) =>
        FindClaim(settings, device) is not null;

    /// <summary>The claim covering this endpoint, or null when the cable is not ours.</summary>
    public static CableClaimSettings? FindClaim(AppSettings settings, AudioDeviceInfo device) =>
        settings.ClaimedCables.FirstOrDefault(c =>
            string.Equals(c.RenderEndpointId, device.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CaptureEndpointId, device.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Takes ownership of a cable LoopIt7 installed and gives both of its ends a LoopIt7 name.
    /// <para>
    /// The caller is responsible for only offering this for a cable LoopIt7 put there itself.
    /// The guards here catch the cases that can be seen from inside the app: the endpoints have
    /// to be a recognised cable, they have to be the two ends of one, and neither may already
    /// be claimed.
    /// </para>
    /// </summary>
    public static bool TryClaim(
        AppSettings settings,
        AudioDeviceInfo render,
        AudioDeviceInfo capture,
        string name,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A cable needs a name.";
            return false;
        }

        if (!VirtualCableService.IsVirtual(render) || !VirtualCableService.IsVirtual(capture))
        {
            error = "That is a real device, not a cable. LoopIt7 only renames cables it installed.";
            return false;
        }

        if (!MayClaim(settings, render) || !MayClaim(settings, capture))
        {
            error = "That cable was already on this machine. LoopIt7 leaves those named the way their owner set them.";
            return false;
        }

        if (FindClaim(settings, render) is not null || FindClaim(settings, capture) is not null)
        {
            error = "That cable is already named by LoopIt7.";
            return false;
        }

        // Read both ends before touching either, so a failure halfway leaves nothing to undo.
        var renderNames = EndpointNaming.TryRead(render);
        var captureNames = EndpointNaming.TryRead(capture);

        if (renderNames is null || captureNames is null)
        {
            error = "Windows has no record of one end of that cable.";
            return false;
        }

        var (feedName, pickupName) = NamesFor(name, VirtualCableService.VendorOf(render));

        if (!EndpointNaming.TryWrite(render, feedName, out error)) return false;

        if (!EndpointNaming.TryWrite(capture, pickupName, out error))
        {
            // Put the end that did take back, rather than leaving the cable half renamed.
            EndpointNaming.TryWrite(render, renderNames, out _);
            return false;
        }

        settings.ClaimedCables.Add(new CableClaimSettings
        {
            RenderEndpointId = render.Id,
            CaptureEndpointId = capture.Id,
            ClaimedName = feedName.Name,
            OriginalRenderName = renderNames.Name,
            OriginalRenderInterface = renderNames.InterfaceName,
            OriginalCaptureName = captureNames.Name,
            OriginalCaptureInterface = captureNames.InterfaceName
        });

        error = null;
        return true;
    }

    /// <summary>
    /// Hands a cable back, restoring the names it had before LoopIt7 touched it. Called when the
    /// user drops the cable and by the uninstaller, because a device left carrying our name
    /// after LoopIt7 is gone is exactly the kind of litter nobody can trace later.
    /// <para>
    /// The claim is dropped whether or not the rename succeeded. An endpoint that has since been
    /// removed cannot be restored and should not keep a claim alive for the rest of time.
    /// </para>
    /// </summary>
    public static bool TryRelease(
        AppSettings settings,
        CableClaimSettings claim,
        IEnumerable<AudioDeviceInfo> devices,
        out string? error)
    {
        var byId = devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        string? failure = Restore(byId, claim.RenderEndpointId, claim.OriginalRenderName, claim.OriginalRenderInterface);
        failure ??= Restore(byId, claim.CaptureEndpointId, claim.OriginalCaptureName, claim.OriginalCaptureInterface);

        settings.ClaimedCables.Remove(claim);

        error = failure;
        return failure is null;
    }

    /// <summary>
    /// Puts one end back. A missing endpoint is not a failure: the cable was uninstalled, and
    /// there is nothing left to rename.
    /// </summary>
    private static string? Restore(
        Dictionary<string, AudioDeviceInfo> byId,
        string endpointId,
        string name,
        string interfaceName)
    {
        if (!byId.TryGetValue(endpointId, out var device)) return null;

        return EndpointNaming.TryWrite(device, new EndpointNames(name, interfaceName), out string? failure)
            ? null
            : failure;
    }

    /// <summary>
    /// Releases every claim. The uninstaller's last act, and the safety net for a machine where
    /// somebody deleted the settings file by hand and reinstalled.
    /// </summary>
    public static void ReleaseAll(AppSettings settings, IEnumerable<AudioDeviceInfo> devices)
    {
        var snapshot = devices.ToList();

        foreach (var claim in settings.ClaimedCables.ToList())
        {
            TryRelease(settings, claim, snapshot, out _);
        }
    }
}
