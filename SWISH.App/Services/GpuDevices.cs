using System.Runtime.InteropServices;

namespace Swish.App.Services;

/// <summary>
/// The machine's GPUs, from DXGI. Their order is the adapter index DirectML takes, so the index goes
/// straight to <see cref="HandGestureRecognition.GestureOptions.GpuAdapter"/>. Software renderers are left out.
/// </summary>
public static class GpuDevices
{
    /// <param name="Discrete">A dedicated card (has its own video memory) rather than graphics built into the CPU.</param>
    public sealed record Gpu(int Index, string Name, bool Discrete);

    public static IReadOnlyList<Gpu> List()
    {
        var result = new List<Gpu>();
        try
        {
            Check(CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory));
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) >= 0; i++)
                {
                    try
                    {
                        Check(adapter.GetDesc1(out var desc));
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;   // "Microsoft Basic Render Driver"
                        // Integrated GPUs report a token amount of dedicated memory (they share system RAM).
                        bool discrete = (ulong)desc.DedicatedVideoMemory >= 512UL * 1024 * 1024;
                        result.Add(new Gpu((int)i, CleanName(desc.Description), discrete));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception) { /* no DXGI: CPU only */ }
        return result;
    }

    /// <summary>"Intel(R) Graphics" → "Intel Graphics".</summary>
    static string CleanName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(name, @"[®™©]|\((R|TM|C)\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            @"\s+", " ").Trim();

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // ---- DXGI interop (vtables declared in order up to the methods used) ----
    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIFactory
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIAdapter
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
