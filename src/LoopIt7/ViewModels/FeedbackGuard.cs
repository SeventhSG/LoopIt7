using LoopIt7.Audio;
using LoopIt7.Audio.Graph;

namespace LoopIt7.ViewModels;

/// <summary>
/// The rule that refuses a patch which would let audio arrive back where it came from.
/// <para>
/// It lives on its own, away from the view model, for one reason: a feedback loop reaches full
/// scale in under a second in whatever somebody is wearing, so this rule has to be testable
/// without a window, a settings file or a single audio device being opened. Everything it needs
/// arrives as arguments.
/// </para>
/// </summary>
public static class FeedbackGuard
{
    /// <summary>
    /// Whether patching this pair would let audio arrive back where it came from. Three ways
    /// that can happen: a chain of virtual outputs that closes on itself, a device tapped in
    /// loopback that reaches that same device again, and a cable whose recording end comes back
    /// round to its own playback end, however many boxes are in between.
    /// </summary>
    public static bool WouldFeedBack(
        PatchNodeViewModel from,
        PatchNodeViewModel to,
        IReadOnlyList<CableViewModel> cables,
        IReadOnlyList<SourceNodeViewModel> sources,
        IReadOnlyList<AudioDeviceInfo> inputDevices,
        IReadOnlyList<AudioDeviceInfo> outputDevices,
        out string reason)
    {
        if (ReferenceEquals(from, to) || Downstream(to, cables).Contains(from))
        {
            reason = $"That would run {to.Title} back into itself. Audio going round a loop with nothing in the way reaches full scale almost at once.";
            return true;
        }

        foreach (var tap in sources.Where(s => s.Kind == SourceKind.DeviceLoopback))
        {
            foreach (var node in Downstream(tap, cables, from, to))
            {
                if (node is not DestinationNodeViewModel endpoint) continue;
                if (endpoint.DeviceId != tap.DeviceId) continue;

                reason = $"That would feed {endpoint.Title} back into itself through {tap.Title}. You are already hearing this audio there.";
                return true;
            }
        }

        // A cable is two endpoints Windows says nothing about to each other, so audio sent to
        // its playback end and picked up at its recording end looks like two unrelated devices
        // right up until it is going round with only a driver in the way.
        foreach (var pickup in sources.Where(s => s.Kind == SourceKind.Device))
        {
            var device = inputDevices.FirstOrDefault(
                d => d.Id == pickup.DeviceId && d.Kind == AudioSourceKind.Capture);

            if (device is null || !VirtualCableService.IsVirtual(device)) continue;

            var feeds = VirtualCableService.FindFeedEndpoints(device, outputDevices);
            if (feeds.Count == 0) continue;

            foreach (var node in Downstream(pickup, cables, from, to))
            {
                if (node is not DestinationNodeViewModel endpoint) continue;
                if (!feeds.Any(f => string.Equals(f.Id, endpoint.DeviceId, StringComparison.OrdinalIgnoreCase))) continue;

                reason = $"That would send {endpoint.Title} straight back to {pickup.Title}. They are the two ends of one cable, so the audio would go round and round.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Everything the audio leaving this box can reach, following cables through any virtual
    /// outputs on the way. The optional extra edge is the cable being considered, so a patch
    /// can be judged before it exists.
    /// </summary>
    private static HashSet<PatchNodeViewModel> Downstream(
        PatchNodeViewModel start,
        IReadOnlyList<CableViewModel> cables,
        PatchNodeViewModel? extraFrom = null,
        PatchNodeViewModel? extraTo = null)
    {
        var seen = new HashSet<PatchNodeViewModel>();
        var pending = new Stack<PatchNodeViewModel>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node)) continue;

            // Only a source or a virtual output passes anything on. A real endpoint is the end.
            if (!node.CanSend) continue;

            foreach (var cable in cables)
            {
                if (cable.Source == node) pending.Push(cable.Destination);
            }

            if (extraFrom is not null && extraTo is not null && node == extraFrom) pending.Push(extraTo);
        }

        return seen;
    }
}
