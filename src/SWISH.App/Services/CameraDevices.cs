using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Swish.App.Services;

/// <summary>
/// Lists video capture devices through Windows Media Foundation. That's the backend OpenCV uses on
/// Windows, so the list order matches the camera index the gesture engine opens. (No Windows-10 SDK
/// target needed, unlike the WinRT device APIs.)
/// </summary>
public static class CameraDevices
{
    public sealed record Camera(int Index, string Name, string FullName);

    public static IReadOnlyList<Camera> List()
    {
        var names = new List<string>();
        try
        {
            MFStartup(MF_VERSION, 0);
            Check(MFCreateAttributes(out var attrs, 1));
            var sourceType = SourceTypeKey;
            var vidcap = VidcapGuid;
            Check(attrs.SetGUID(ref sourceType, ref vidcap));
            Check(MFEnumDeviceSources(attrs, out var array, out uint count));
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var unknown = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    try
                    {
                        var device = (IMFAttributes)Marshal.GetObjectForIUnknown(unknown);
                        var key = FriendlyNameKey;
                        names.Add(device.GetAllocatedString(ref key, out var name, out _) == 0 ? name : $"Camera {i + 1}");
                        Marshal.ReleaseComObject(device);
                    }
                    finally { Marshal.Release(unknown); }
                }
            }
            finally { Marshal.FreeCoTaskMem(array); }
            Marshal.ReleaseComObject(attrs);
        }
        catch (Exception)
        {
            // Media Foundation missing (e.g. Windows N without the media pack): fall back to numbered cameras.
            if (names.Count == 0) names.Add("Camera 1");
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return names.Select((full, i) =>
        {
            var shortName = Shorten(full);
            seen[shortName] = seen.GetValueOrDefault(shortName) + 1;
            return new Camera(i, seen[shortName] > 1 ? $"{shortName} {seen[shortName]}" : shortName, full);
        }).ToList();
    }

    /// <summary>"Integrated Camera (04f2:b6dd)" -> "Integrated Camera": hardware ids and trademark signs dropped, never cut short.</summary>
    public static string Shorten(string name)
    {
        var s = Regex.Replace(name, @"\s*[\(\[].*?[\)\]]\s*$", "").Trim();   // trailing (vid:pid) / [id]
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @"[®™©]", "");
        return s.Length == 0 ? name.Trim() : s;
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // ---- Media Foundation interop ----
    const uint MF_VERSION = 0x00020070;
    static readonly Guid SourceTypeKey = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");     // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE
    static readonly Guid VidcapGuid = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");        // ..._SOURCE_TYPE_VIDCAP_GUID
    static readonly Guid FriendlyNameKey = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");   // MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mf.dll")] static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr sources, out uint count);

    // IMFAttributes, declared in vtable order up to the methods used (all [PreserveSig]).
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int type, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr buffer, uint size, out uint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr obj);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }
}
