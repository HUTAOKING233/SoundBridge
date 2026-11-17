using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SoundBridge.Core;

internal static class A2dpCaptureEndpoint
{
    private const uint Active = 1;
    private const uint InProcessServer = 1;
    private const float CaptureVolume = 0.5f;
    private const int InitialChecks = 8;
    private const int InitialIntervalMs = 250;
    private const int IdleIntervalMs = 2000;
    private const float VolumeTolerance = 0.001f;
    private static readonly Guid EventContext = new("8D629B05-85B8-4EC8-9B8C-FD482EF56946");

    // On Windows A2DP sink endpoints, this property stores the originating audio
    // interface path with a "{2}." prefix. Match the entire path, including \SNK.
    private const string SourceInterface = "{233164c8-1b2c-4c7d-bc68-b671687a2567},1";
    private const string EndpointInstance = "{9c119480-ddc2-4954-a150-5bd240d454ad},10";
    private const string EndpointInstancePrefix = @"SWD\MMDEVAPI\";
    private const string CaptureRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

    internal static async Task ConfigureAsync(
        string deviceId, Func<bool> isConnected, bool recoverSound, Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            log?.Invoke("A2DP capture: looking for the matching active endpoint");
            int checks = 0;
            while (isConnected())
            {
                string? endpointId = null;
                try
                {
                    endpointId = TrySetCaptureVolume(deviceId, isConnected, log);
                }
                catch (COMException ex)
                {
                    if (checks == 0)
                        log?.Invoke($"A2DP capture not ready: {ex.Message} (0x{ex.HResult:X8})");
                }

                if (endpointId is not null)
                {
                    if (recoverSound)
                        await A2dpRenderSession.RecoverAsync(endpointId, isConnected, log, cancellationToken)
                            .ConfigureAwait(false);
                    return;
                }

                if (checks < InitialChecks)
                {
                    checks++;
                    if (checks == InitialChecks && isConnected())
                        log?.Invoke("A2DP capture: still waiting for the endpoint to accept 50%");
                }

                await Task.Delay(checks < InitialChecks ? InitialIntervalMs : IdleIntervalMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            log?.Invoke("A2DP capture: stopped waiting because the connection closed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("A2DP capture: stopped waiting because the connection closed");
        }
        catch (Exception ex)
        {
            log?.Invoke($"A2DP capture setup failed: {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    private static string? TrySetCaptureVolume(string deviceId, Func<bool> isConnected, Action<string>? log)
    {
        // The A2DP sink can be hidden from normal enumeration. Read its stored
        // interface association, then open the endpoint by its Core Audio ID.
        using RegistryKey? capture = Registry.LocalMachine.OpenSubKey(CaptureRegistryPath);
        if (capture is null) return null;

        string? match = null;
        foreach (string subKey in capture.GetSubKeyNames())
        {
            // Windows can keep old endpoint records after the same Bluetooth
            // device is unpaired and paired again.
            using RegistryKey? record = capture.OpenSubKey(subKey);
            if (record?.GetValue("DeviceState") is not int recordedState || recordedState != Active)
                continue;

            using RegistryKey? properties = record.OpenSubKey("Properties");
            if (properties?.GetValue(SourceInterface) is not string path
                || !string.Equals(path, "{2}." + deviceId, StringComparison.OrdinalIgnoreCase)
                || properties.GetValue(EndpointInstance) is not string instance
                || !instance.StartsWith(EndpointInstancePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null) return null;
            match = instance[EndpointInstancePrefix.Length..];
        }

        if (match is null || !isConnected()) return null;

        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDevice? endpoint = null;
        try
        {
            enumerator.GetDevice(match, out endpoint);
            endpoint.GetState(out uint state);
            return state == Active && SetCaptureVolume(endpoint, isConnected, log) ? match : null;
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static bool SetCaptureVolume(IDevice endpoint, Func<bool> isConnected, Action<string>? log)
    {
        Guid iid = typeof(IEndpointVolume).GUID;
        endpoint.Activate(ref iid, InProcessServer, IntPtr.Zero, out object control);
        try
        {
            var volume = (IEndpointVolume)control;
            if (!isConnected()) return false;
            log?.Invoke("A2DP capture: matching endpoint is active");

            Guid context = EventContext;
            volume.GetMasterVolumeLevelScalar(out float before);
            if (Math.Abs(before - CaptureVolume) >= VolumeTolerance)
                volume.SetMasterVolumeLevelScalar(CaptureVolume, ref context);
            volume.GetMasterVolumeLevelScalar(out float after);
            if (Math.Abs(after - CaptureVolume) >= VolumeTolerance) return false;
            log?.Invoke($"A2DP capture level: {before:P1} -> {after:P1}");
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(control);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class DeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint stateMask, out IDeviceCollection endpoints);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice endpoint);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
    {
        void Activate(ref Guid iid, uint classContext, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out IntPtr properties);
        void GetId(out IntPtr id);
        void GetState(out uint state);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IDevice endpoint);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr callback);
        void UnregisterControlChangeNotify(IntPtr callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
