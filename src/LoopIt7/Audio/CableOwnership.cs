using LoopIt7.Models;

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

        var claimed = new EndpointNames(name.Trim(), OwnedInterfaceName);

        if (!EndpointNaming.TryWrite(render, claimed, out error)) return false;

        if (!EndpointNaming.TryWrite(capture, claimed, out error))
        {
            // Put the end that did take back, rather than leaving the cable half renamed.
            EndpointNaming.TryWrite(render, renderNames, out _);
            return false;
        }

        settings.ClaimedCables.Add(new CableClaimSettings
        {
            RenderEndpointId = render.Id,
            CaptureEndpointId = capture.Id,
            ClaimedName = claimed.Name,
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
