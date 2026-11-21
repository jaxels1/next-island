// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;

namespace SapphireGameHud;

internal sealed class SystemAudio : IDisposable
{
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 23;
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private IMMDeviceEnumerator? enumerator;
    private IAudioEndpointVolume? volume;
    private bool disposed;

    internal bool Available => EnsureVolume() is not null;

    internal (double Level, bool Muted)? Read()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = EnsureVolume();
            if (endpoint is null) return null;
            try
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level));
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
                return (Math.Clamp(level, 0, 1), muted);
            }
            catch (COMException error) when (error.ErrorCode == DeviceInvalidated && attempt == 0)
            {
                ReleaseVolume();
            }
            catch (Exception error) when (IsAudioError(error))
            {
                ReleaseVolume();
                return null;
            }
        }
        return null;
    }

    internal bool SetLevel(double level)
    {
        var clamped = (float)Math.Clamp(level, 0, 1);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = EnsureVolume();
            if (endpoint is null) return false;
            try
            {
                var context = Guid.Empty;
                Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(clamped, ref context));
                if (clamped > 0.001f && endpoint.GetMute(out var muted) == 0 && muted)
                    endpoint.SetMute(false, ref context);
                return true;
            }
            catch (COMException error) when (error.ErrorCode == DeviceInvalidated && attempt == 0)
            {
                ReleaseVolume();
            }
            catch (Exception error) when (IsAudioError(error))
            {
                ReleaseVolume();
                return false;
            }
        }
        return false;
    }

    internal bool SetMuted(bool muted)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = EnsureVolume();
            if (endpoint is null) return false;
            try
            {
                var context = Guid.Empty;
                Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref context));
                return true;
            }
            catch (COMException error) when (error.ErrorCode == DeviceInvalidated && attempt == 0)
            {
                ReleaseVolume();
            }
            catch (Exception error) when (IsAudioError(error))
            {
                ReleaseVolume();
                return false;
            }
        }
        return false;
    }

    internal (double Level, bool Muted)? Adjust(double delta)
    {
        var current = Read();
        if (current is not var (level, _)) return null;
        var next = Math.Clamp(level + delta, 0, 1);
        return SetLevel(next) ? Read() ?? (next, false) : null;
    }

    private IAudioEndpointVolume? EnsureVolume()
    {
        if (disposed) return null;
        if (volume is not null) return volume;

        IMMDevice? device = null;
        try
        {
            if (enumerator is null)
            {
                var type = Type.GetTypeFromCLSID(EnumeratorClsid);
                if (type is null) return null;
                enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
                if (enumerator is null) return null;
            }

            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device) != 0 || device is null)
                return null;

            var iid = EndpointVolumeIid;
            if (device.Activate(ref iid, ClsCtxAll, 0, out var activated) != 0)
                return null;

            volume = activated as IAudioEndpointVolume;
            if (volume is null && activated is not null && Marshal.IsComObject(activated))
                Marshal.ReleaseComObject(activated);
            return volume;
        }
        catch (Exception error) when (IsAudioError(error))
        {
            ReleaseVolume();
            return null;
        }
        finally
        {
            if (device is not null && Marshal.IsComObject(device))
                Marshal.ReleaseComObject(device);
        }
    }

    private void ReleaseVolume()
    {
        if (volume is not null && Marshal.IsComObject(volume))
            Marshal.ReleaseComObject(volume);
        volume = null;
    }

    private static bool IsAudioError(Exception error) =>
        error is COMException or InvalidCastException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseVolume();
        if (enumerator is not null && Marshal.IsComObject(enumerator))
            Marshal.ReleaseComObject(enumerator);
        enumerator = null;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
