namespace LoopIt7.Audio.Graph;

/// <summary>What a source node is listening to.</summary>
public enum SourceKind
{
    /// <summary>A recording endpoint: microphone, line in, interface input.</summary>
    Device,

    /// <summary>A playback endpoint tapped in loopback, so we hear everything it plays.</summary>
    DeviceLoopback,

    /// <summary>One application and its children, captured on their own.</summary>
    Application,

    /// <summary>
    /// A virtual output: cables arrive at it, it sums them, and the sum leaves again. It
    /// produces audio like any other source, but the audio comes from the patchbay itself
    /// rather than from anything Windows owns.
    /// </summary>
    Bus
}

public enum NodeStatus
{
    Idle,
    Live,

    /// <summary>The endpoint or process is gone for now. The node stays and waits for it.</summary>
    Waiting,

    Failed
}

public sealed class GraphErrorEventArgs(string nodeId, string message) : EventArgs
{
    public string NodeId { get; } = nodeId;
    public string Message { get; } = message;
}
