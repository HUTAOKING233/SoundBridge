namespace SoundBridge.Core;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed
}

public sealed record AudioDevice(string Id, string Name);

public sealed record ConnectionUpdate(string DeviceId, ConnectionState State, string? Error);
