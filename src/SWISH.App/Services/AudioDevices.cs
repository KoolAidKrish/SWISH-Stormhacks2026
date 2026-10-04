using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VoiceKeys;

namespace Swish.App.Services;

/// <summary>
/// Microphones with names people recognise. The capture code (WinMM) numbers devices but only knows the
/// first 31 characters of each name ("Microphone Array (Intel® Smart "), so the full names come from
/// Core Audio and are matched to the WinMM devices by that prefix.
/// </summary>
public static class AudioDevices
{
    /// <param name="Index">The WinMM device number the voice engine records from.</param>
    /// <param name="Name">Readable name, e.g. "Microphone Array (Intel Smart Sound Technology)".</param>
    /// <param name="FullName">Everything Windows calls it, for a tooltip.</param>
    public sealed record Mic(int Index, string Name, string FullName);

    public static IReadOnlyList<Mic> List()
    {
        var endpoints = Endpoints();
        var used = new HashSet<int>();
        return MicCapture.ListDevices().Select(d =>
        {
            int match = endpoints.FindIndex(e => !used.Contains(endpoints.IndexOf(e)) &&
                                                 e.FriendlyName.StartsWith(d.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match < 0) return new Mic(d.Index, Readable(d.Name, null), d.Name);
            used.Add(match);
            var e = endpoints[match];
            return new Mic(d.Index, Readable(e.Description, e.Adapter), e.FriendlyName);
        }).ToList();
    }

    /// <summary>
    /// "Microphone Array" + "Intel® Smart Sound Technology for Digital Microphones" →
    /// "Microphone Array (Intel Smart Sound Technology)": trademark signs, "2- " prefixes and
    /// "for ..." tails dropped, nothing cut mid-word.
    /// </summary>
    public static string Readable(string description, string? adapter)
    {
        static string Clean(string s)
        {
            s = Regex.Replace(s, @"[®™©]|\((R|TM|C)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^\d+\s*-\s*", "");                         // "2- USB Audio Device"
            s = Regex.Replace(s, @"\s+for\s+.*$", "", RegexOptions.IgnoreCase); // "... for Digital Microphones"
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
        var desc = Clean(description);
        var source = adapter is null ? "" : Clean(adapter);
        if (desc.Length == 0) return source.Length > 0 ? source : description.Trim();
        return source.Length == 0 || source.Equals(desc, StringComparison.OrdinalIgnoreCase) ? desc : $"{desc} ({source})";
    }

    // ---- Core Audio ----

    sealed record Endpoint(string FriendlyName, string Description, string? Adapter);

    static List<Endpoint> Endpoints()
    {
        var list = new List<Endpoint>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Check(enumerator.EnumAudioEndpoints(1 /* eCapture */, 1 /* DEVICE_STATE_ACTIVE */, out var devices));
            Check(devices.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                Check(devices.Item(i, out var device));
                Check(device.OpenPropertyStore(0 /* STGM_READ */, out var props));
                var friendly = GetString(props, FriendlyNameKey);
                if (friendly is null) continue;
                list.Add(new Endpoint(friendly, GetString(props, DescriptionKey) ?? friendly, GetString(props, AdapterKey)));
            }
        }
        catch (Exception) { /* fall back to the WinMM names */ }
        return list;
    }

    static string? GetString(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);   // PKEY_Device_FriendlyName
    static readonly PropertyKey DescriptionKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);     // PKEY_Device_DeviceDesc
    static readonly PropertyKey AdapterKey = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);         // PKEY_DeviceInterface_FriendlyName

    [StructLayout(LayoutKind.Sequential)]
    readonly struct PropertyKey(Guid format, uint id) { readonly Guid _format = format; readonly uint _id = id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate();
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount();
        void GetAt();
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
}
