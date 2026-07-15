using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DSPiConsole.Services;

/// <summary>
/// Reads the current channel count of the DSPi's Windows audio endpoint.
///
/// The DSPi presents itself to Windows as a playback (render) device
/// ("Speakers (… DSPi)"). The channel count of that device's selected format —
/// the "alt mode" the user picks under Sound Settings → Device Properties →
/// Advanced (2-channel vs multichannel) — is the number of channels the PC
/// streams into the DSPi over USB, i.e. the DSPi's USB input channel count.
///
/// This queries the Core Audio MMDevice API directly (no external dependency).
/// The channel count comes from the endpoint's <c>PKEY_AudioEngine_DeviceFormat</c>
/// (the selected default format), which follows the user's alt-mode choice
/// without any audio having to play — matching the macOS app's CoreAudio path.
/// </summary>
public static class WindowsAudioDevices
{
    /// <summary>Channel count of the active render endpoint whose friendly name
    /// contains <paramref name="nameMatch"/> (default "DSPi"). Prefers the default
    /// render device when it matches, else the first active match. Null if no
    /// matching endpoint is present or the query fails.</summary>
    public static int? GetDspiRenderChannelCount(string nameMatch = "DSPi")
    {
        try
        {
            EnsureComInitialized();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            // Prefer the default render endpoint if it's the DSPi — that's the
            // one actually receiving playback.
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var def) == 0 && def != null)
            {
                var (name, ch) = ReadEndpoint(def);
                Marshal.ReleaseComObject(def);
                if (ch > 0 && name != null && name.Contains(nameMatch, StringComparison.OrdinalIgnoreCase))
                    return ch;
            }

            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection == null)
                return null;

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var dev) != 0 || dev == null) continue;
                var (name, ch) = ReadEndpoint(dev);
                Marshal.ReleaseComObject(dev);
                if (ch > 0 && name != null && name.Contains(nameMatch, StringComparison.OrdinalIgnoreCase))
                {
                    Marshal.ReleaseComObject(collection);
                    Marshal.ReleaseComObject(enumerator);
                    return ch;
                }
            }
            Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            // Any COM failure → treat as "unknown"; the caller falls back to the
            // device-reported channel count.
        }
        return null;
    }

    /// <summary>Diagnostic: every active render endpoint's (name, channel count).</summary>
    public static List<(string name, int channels)> ListRenderEndpoints()
    {
        var list = new List<(string, int)>();
        try
        {
            EnsureComInitialized();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out var collection) == 0 && collection != null)
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var dev) != 0 || dev == null) continue;
                    var (name, ch) = ReadEndpoint(dev);
                    Marshal.ReleaseComObject(dev);
                    list.Add((name ?? "?", ch));
                }
                Marshal.ReleaseComObject(collection);
            }
            Marshal.ReleaseComObject(enumerator);
        }
        catch { }
        return list;
    }

    private static (string? name, int channels) ReadEndpoint(IMMDevice device)
    {
        string? name = null;
        int channels = 0;
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store == null)
            return (null, 0);
        try
        {
            var keyName = PKEY_Device_FriendlyName;
            if (store.GetValue(ref keyName, out var vName) == 0)
            {
                if (vName.vt == VT_LPWSTR && vName.ptr != IntPtr.Zero)
                    name = Marshal.PtrToStringUni(vName.ptr);
                PropVariantClear(ref vName);
            }

            var keyFmt = PKEY_AudioEngine_DeviceFormat;
            if (store.GetValue(ref keyFmt, out var vFmt) == 0)
            {
                // VT_BLOB: the blob is a WAVEFORMATEX; nChannels is the uint16 at
                // byte offset 2. (BLOB.pBlobData sits at PROPVARIANT offset 16 on x64.)
                if (vFmt.vt == VT_BLOB && vFmt.blobData != IntPtr.Zero && vFmt.blobSize >= 4)
                    channels = Marshal.ReadInt16(vFmt.blobData, 2);
                PropVariantClear(ref vFmt);
            }
        }
        finally { Marshal.ReleaseComObject(store); }
        return (name, channels);
    }

    private static void EnsureComInitialized()
    {
        // Robust from any thread: S_OK / S_FALSE (already init) / RPC_E_CHANGED_MODE
        // (thread already in a different apartment — COM is up either way) are all fine.
        CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
    }

    // ── Native constants ─────────────────────────────────────────────────────
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint STGM_READ = 0x00000000;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BLOB = 65;
    private const uint COINIT_MULTITHREADED = 0x0;

    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };
    private static PROPERTYKEY PKEY_AudioEngine_DeviceFormat = new()
    {
        fmtid = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"),
        pid = 0
    };

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    // ── COM interop ──────────────────────────────────────────────────────────
    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // x64 PROPVARIANT: vt @0, LPWSTR ptr @8, BLOB { cbSize @8, pBlobData @16 }.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;       // VT_LPWSTR pwszVal
        [FieldOffset(8)] public uint blobSize;    // VT_BLOB cbSize
        [FieldOffset(16)] public IntPtr blobData; // VT_BLOB pBlobData
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }
}
