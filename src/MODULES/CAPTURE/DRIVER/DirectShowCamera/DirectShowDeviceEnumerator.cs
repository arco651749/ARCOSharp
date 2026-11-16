using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ARCOS.Driver.DirectShowCamera;

/// <summary>
/// 描述透過 Windows DirectShow 列舉出的視訊擷取裝置資訊。
/// </summary>
public sealed record DirectShowDeviceInfo(int Index, string FriendlyName, string DevicePath)
{
    public string DisplayName => $"{Index}: {FriendlyName}";
}

/// <summary>
/// Windows DirectShow 原生視訊擷取裝置列舉器 (ICreateDevEnum + CLSID_VideoInputDeviceCategory)。
/// 依序取得與 OpenCV VideoCaptureAPIs.DSHOW 一致的裝置索引與友善名稱，無需額外第三方相依套件。
/// </summary>
public static class DirectShowDeviceEnumerator
{
    private static readonly Guid ClsidSystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid ClsidVideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid IidIPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    private const uint CoInitApartmentThreaded = 0x2;
    private const uint CoInitMultiThreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>
    /// 列舉目前本機所有 Windows DirectShow Video Input 裝置。非 Windows 平台或失敗時安全回傳空清單。
    /// </summary>
    public static IReadOnlyList<DirectShowDeviceInfo> EnumerateVideoInputDevices()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<DirectShowDeviceInfo>();

        var result = new List<DirectShowDeviceInfo>();
        bool shouldUninit = false;

        try
        {
            int hrInit = CoInitializeEx(IntPtr.Zero, CoInitMultiThreaded);
            if (hrInit == RpcEChangedMode)
            {
                // Thread already initialized as STA (e.g. Avalonia UI thread); proceed cleanly without re-initializing.
            }
            else if (hrInit >= 0)
            {
                shouldUninit = true;
            }

            var enumType = Type.GetTypeFromCLSID(ClsidSystemDeviceEnum);
            if (enumType == null) return result;

            object? comObj = Activator.CreateInstance(enumType);
            if (comObj is not ICreateDevEnum devEnum) return result;

            try
            {
                Guid category = ClsidVideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref category, out IEnumMoniker? enumMoniker, 0);
                if (hr != 0 || enumMoniker == null)
                    return result;

                try
                {
                    var monikers = new IMoniker[1];
                    int index = 0;

                    while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0 && monikers[0] != null)
                    {
                        IMoniker moniker = monikers[0];
                        try
                        {
                            Guid bagIid = IidIPropertyBag;
                            moniker.BindToStorage(null!, null!, ref bagIid, out object bagObj);
                            if (bagObj is IPropertyBag propBag)
                            {
                                try
                                {
                                    string friendly = ReadStringProp(propBag, "FriendlyName");
                                    if (string.IsNullOrWhiteSpace(friendly))
                                        friendly = $"DirectShow Camera {index}";
                                    string devPath = ReadStringProp(propBag, "DevicePath");
                                    result.Add(new DirectShowDeviceInfo(index, friendly, devPath));
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(propBag);
                                }
                            }
                            index++;
                        }
                        catch
                        {
                            index++;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(moniker);
                            monikers[0] = null!;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumMoniker);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(devEnum);
            }
        }
        catch
        {
            // Keep enumeration resilient when COM / DirectShow runtime is unavailable.
        }
        finally
        {
            if (shouldUninit)
            {
                CoUninitialize();
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 "0"、"0: Integrated Camera" 或裝置名稱字串為對應的 DirectShow 裝置索引。
    /// </summary>
    public static int ResolveDeviceIndex(string? spec, IReadOnlyList<DirectShowDeviceInfo>? devices = null)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return 0;

        string trimmed = spec.Trim();

        // 1. 純數字或 "Index: Name" 格式
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0 && int.TryParse(trimmed[..colonIdx].Trim(), out int prefixIdx))
            return Math.Max(0, prefixIdx);

        if (int.TryParse(trimmed, out int directIdx))
            return Math.Max(0, directIdx);

        // 2. 以友善名稱比對列舉結果
        var list = devices ?? EnumerateVideoInputDevices();
        var exact = list.FirstOrDefault(d => d.FriendlyName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Index;

        var partial = list.FirstOrDefault(d => d.FriendlyName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        if (partial != null) return partial.Index;

        return 0;
    }

    private static string ReadStringProp(IPropertyBag bag, string propName)
    {
        object? val = null;
        int hr = bag.Read(propName, ref val, IntPtr.Zero);
        return hr >= 0 && val != null ? val.ToString()?.Trim() ?? string.Empty : string.Empty;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            [In] ref Guid pType,
            [Out] out IEnumMoniker? ppEnumMoniker,
            [In] int dwFlags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? pVar,
            [In] IntPtr pErrorLog);

        [PreserveSig]
        int Write(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }
}
