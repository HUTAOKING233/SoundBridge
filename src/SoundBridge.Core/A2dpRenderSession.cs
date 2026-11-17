using System.Runtime.InteropServices;
using static SoundBridge.Core.A2dpCaptureEndpoint;

namespace SoundBridge.Core;

internal static class A2dpRenderSession
{
    private const int Render = 0;
    private const uint ActiveEndpoint = 1;
    private const int ActiveSession = 1;
    private const uint InProcessServer = 1;
    private const int MuteDurationMs = 120;
    private static readonly Guid EventContext = new("978853BB-CB53-443C-949D-F2D5E072D226");

    internal static async Task RecoverAsync(
        string captureEndpointId, Func<bool> isConnected, Action<string>? log, CancellationToken cancellationToken)
    {
        int guidStart = captureEndpointId.LastIndexOf('{');
        if (guidStart < 0 || !Guid.TryParse(captureEndpointId.AsSpan(guidStart), out Guid captureGuid))
        {
            log?.Invoke("A2DP render: could not identify the capture endpoint");
            return;
        }

        // Windows gives the A2DP relay session a suffix containing its capture
        // endpoint GUID. This avoids picking a session by name or by svchost PID.
        string sessionSuffix = "%b" + captureGuid.ToString("B");
        log?.Invoke("A2DP render: waiting for the matching active mixer session");
        int checks = 0;
        while (isConnected())
        {
            try
            {
                if (TryRecover(sessionSuffix, isConnected, log)) return;
            }
            catch (COMException ex)
            {
                if (checks == 0)
                    log?.Invoke($"A2DP render session not ready: {ex.Message} (0x{ex.HResult:X8})");
            }

            if (checks < 6) checks++;
            await Task.Delay(checks < 6 ? 500 : 2000, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryRecover(string suffix, Func<bool> isConnected, Action<string>? log)
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDeviceCollection? endpoints = null;
        object? match = null;
        try
        {
            enumerator.EnumAudioEndpoints(Render, ActiveEndpoint, out endpoints);
            endpoints.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                endpoints.Item(i, out IDevice endpoint);
                try
                {
                    if (!CollectMatchingSession(endpoint, suffix, ref match)) return false;
                }
                finally
                {
                    Marshal.ReleaseComObject(endpoint);
                }
            }

            if (match is null || !isConnected()) return false;
            ((IAudioSessionControl2)match).GetState(out int state);
            if (state != ActiveSession) return false;
            if (match is not ISimpleAudioVolume volume)
            {
                log?.Invoke("A2DP render: matching mixer session has no mute control");
                return true;
            }

            volume.GetMute(out bool wasMuted);
            if (wasMuted)
            {
                log?.Invoke("A2DP render: mute pulse skipped because the mixer session was already muted");
                return true;
            }

            if (!isConnected()) return false;
            Guid context = EventContext;
            log?.Invoke($"A2DP render: starting a {MuteDurationMs} ms mute pulse on the matching mixer session");
            volume.SetMute(true, ref context);
            try
            {
                Thread.Sleep(MuteDurationMs);
            }
            finally
            {
                volume.SetMute(false, ref context);
            }
            volume.GetMute(out bool mutedAfter);
            log?.Invoke($"A2DP render: mixer mute after recovery = {mutedAfter}");
            return true;
        }
        finally
        {
            if (match is not null) Marshal.ReleaseComObject(match);
            if (endpoints is not null) Marshal.ReleaseComObject(endpoints);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static bool CollectMatchingSession(IDevice endpoint, string suffix, ref object? match)
    {
        Guid iid = typeof(IAudioSessionManager2).GUID;
        endpoint.Activate(ref iid, InProcessServer, IntPtr.Zero, out object manager);
        IAudioSessionEnumerator? sessions = null;
        try
        {
            ((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions);
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out object candidate);
                bool keep = false;
                try
                {
                    if (candidate is not IAudioSessionControl2 session) continue;
                    session.GetState(out int state);
                    if (state != ActiveSession) continue;
                    session.GetSessionIdentifier(out string identifier);
                    if (!identifier.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (match is not null) return false;

                    match = candidate;
                    keep = true;
                }
                finally
                {
                    if (!keep) Marshal.ReleaseComObject(candidate);
                }
            }
            return true;
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(ref Guid id, uint flags, out IntPtr control);
        void GetSimpleAudioVolume(ref Guid id, uint flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState(out int state);
        void GetDisplayName(out IntPtr name);
        void SetDisplayName(IntPtr name, ref Guid context);
        void GetIconPath(out IntPtr path);
        void SetIconPath(IntPtr path, ref Guid context);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(ref Guid group, ref Guid context);
        void RegisterAudioSessionNotification(IntPtr events);
        void UnregisterAudioSessionNotification(IntPtr events);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float value, ref Guid context);
        void GetMasterVolume(out float value);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
