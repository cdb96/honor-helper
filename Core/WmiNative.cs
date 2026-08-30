using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using WmiLight;

namespace HonorHelper;

/// <summary>
/// Thin native bridge over WmiLight's own C library (WmiLight.Native.dll) for the one
/// thing WmiLight's public API cannot do under NativeAOT: the HONOR WMI method
/// <c>OemWMIfun</c> takes a raw <c>uint8[64]</c> input (<c>u8Input</c>) and returns a raw
/// byte array (<c>u8Output</c>). WmiLight exposes no <c>byte[]</c> <c>SetPropertyValue</c>
/// overload, and its high-level <c>ExecuteMethod</c> returns WBEM_E_NOT_FOUND for this
/// instance method, so we drive the call by P/Invoking the exported C ABI of
/// WmiLight.Native.dll directly. Everything else (connect, query, building the in-parameter
/// object) still uses WmiLight's public API.
///
/// IMPORTANT (AOT): we cannot marshal a <c>byte[]</c> through <c>ref object</c> to the
/// VARIANT-typed parameter — under NativeAOT <c>Marshal.GetNativeVariantForObject</c>
/// throws <c>NotSupportedException: VT_ARRAY</c>. So we build the VARIANT + SAFEARRAY
/// ourselves and pass it by <c>ref Variant</c> (pure struct marshaling, AOT-safe).
/// Validated against a real HONOR MagicBook.
/// </summary>
internal static class WmiNative
{
    // ---- WmiLight.Native.dll exported C ABI (stdcall). Signatures mirror WmiLightNative.cpp. ----

    [DllImport("WmiLight.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int Put(
        IntPtr pClassObject,
        [MarshalAs(UnmanagedType.LPWStr)] string wszName,
        ref Variant pVal,
        int type);

    [DllImport("WmiLight.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int ExecMethod(
        IntPtr wbemServices,
        [MarshalAs(UnmanagedType.LPWStr)] string classNameOrPath,
        [MarshalAs(UnmanagedType.LPWStr)] string methodName,
        IntPtr ctx,
        IntPtr inParams,
        out IntPtr outParams);

    [DllImport("WmiLight.Native.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int Get(
        IntPtr pClassObject,
        [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
        ref Variant value,
        ref int pType);

    // ---- WmiLight exposes the underlying native IWbemServices as a private field on WmiConnection.
    // ---- We reflect it out once per connection. It carries the same proxy/auth setup WmiLight
    // ---- applied for the HONOR WMI (required: a raw ConnectServer is access-denied).
    // ---- The trimmer preserves WmiLight's metadata via <TrimmerRootAssembly Include="WmiLight"/>.

    /// <summary>Get the native IWbemServices* from a WmiConnection (must already be "open" by a query).</summary>
    public static IntPtr GetWbemServices(WmiConnection conn)
    {
        var svcField = typeof(WmiConnection).GetField("wbemServices", BindingFlags.NonPublic | BindingFlags.Instance);
        object? svc = svcField?.GetValue(conn);
        return svc is null ? IntPtr.Zero : ReadNativePointer(svc);
    }

    // Walk the type hierarchy (WbemServices : IUnknown) to the nativePointer private field.
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "WmiLight is rooted via TrimmerRootAssembly; its private nativePointer field survives NativeAOT trimming.")]
    [UnconditionalSuppressMessage("Trimming", "IL2050", Justification = "No COM object marshaling in this path; only a raw native pointer is read.")]
    private static IntPtr ReadNativePointer(object instance)
    {
        Type? t = instance.GetType();
        while (t is not null)
        {
            var np = t.GetField("nativePointer", BindingFlags.NonPublic | BindingFlags.Instance);
            if (np is not null)
                return (IntPtr)np.GetValue(instance)!;
            t = t.BaseType;
        }
        return IntPtr.Zero;
    }

    /// <summary>Set a uint8[] property value on a native IWbemClassObject (in-parameters).</summary>
    public static void SetByteArray(WmiMethodParameters inParams, string name, byte[] value)
    {
        // Build a raw VARIANT holding a SAFEARRAY(VT_UI1) of the bytes (AOT-safe: struct marshaling only).
        IntPtr psa = SafeArrayCreateVector(VtUi1, 0, (uint)value.Length);
        if (psa == IntPtr.Zero)
            throw new InvalidOperationException("SafeArrayCreateVector failed");

        try
        {
            if (SafeArrayAccessData(psa, out var pvData) == 0)
            {
                Marshal.Copy(value, 0, pvData, value.Length);
                SafeArrayUnaccessData(psa);
            }

            var variant = new Variant { vt = (ushort)(VtArray | VtUi1), parray = psa };
            int hr = Put((IntPtr)inParams, name, ref variant, CimTypeNone);
            if (hr != 0)
                throw new COMException($"WmiLight.Native Put({name}) failed", hr);
        }
        finally
        {
            SafeArrayDestroy(psa);
        }
    }

    /// <summary>Read a uint8[] property value (u8Output) from a native IWbemClassObject.</summary>
    public static byte[] GetByteArray(IntPtr obj, string name)
    {
        Variant value = default;
        int cimType = 0;
        int hr = Get(obj, name, ref value, ref cimType);
        if (hr != 0)
            throw new COMException($"WmiLight.Native Get({name}) failed", hr);

        try
        {
            // Norm: a uint8[] comes back as VT_ARRAY|VT_UI1 (CimType UInt8|ArrayFlag).
            if ((value.vt & (VtArray | VtUi1)) == (VtArray | VtUi1) ||
                ((cimType & ArrayFlag) != 0 && (cimType & 0xFF) == CimTypeUInt8))
            {
                var psa = value.parray;
                if (psa == IntPtr.Zero)
                    return Array.Empty<byte>();

                SafeArrayGetLBound(psa, 1, out int lb);
                SafeArrayGetUBound(psa, 1, out int ub);
                int len = ub - lb + 1;
                if (len <= 0)
                    return Array.Empty<byte>();

                var result = new byte[len];
                if (SafeArrayAccessData(psa, out var pvData) == 0)
                {
                    Marshal.Copy(pvData, result, 0, len);
                    SafeArrayUnaccessData(psa);
                }
                return result;
            }

            return Array.Empty<byte>();
        }
        finally
        {
            VariantClear(ref value); // VARIANT owns the SAFEARRAY; clear frees it once.
        }
    }

    /// <summary>Invoke OemWMIfun on the HONOR instance. Returns the raw u8Output bytes.</summary>
    public static byte[] Invoke(WmiConnection conn, string instancePath, byte[] command)
    {
        // Build the in-parameters object from the class method signature (public WmiLight API).
        using var method = conn.GetMethod("OemWMIMethod", "OemWMIfun");
        using var inParams = method.CreateInParameters();

        // u8Input is a fixed 64-byte buffer; the CIM layer rejects shorter input.
        var buf = new byte[64];
        Array.Copy(command, buf, Math.Min(command.Length, buf.Length));
        SetByteArray(inParams, "u8Input", buf);

        var services = GetWbemServices(conn);
        if (services == IntPtr.Zero)
            throw new InvalidOperationException("WMI connection is not open.");

        int hr = ExecMethod(services, instancePath, "OemWMIfun", IntPtr.Zero, (IntPtr)inParams, out IntPtr pOut);
        if (hr != 0)
            throw new COMException("OemWMIfun failed", hr);

        try
        {
            return GetByteArray(pOut, "u8Output");
        }
        finally
        {
            Marshal.Release(pOut);
        }
    }

    // ---- VARIANT / CIM type constants ----
    private const ushort VtArray = 0x2000;
    private const ushort VtUi1 = 0x11;
    private const int ArrayFlag = 0x2000;
    private const int CimTypeUInt8 = 17;   // CimType.UInt8
    private const int CimTypeNone = 0;

    // oleaut32 helpers used to build/read the byte SAFEARRAY inside a VARIANT.
    [DllImport("oleaut32.dll")]
    private static extern IntPtr SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(IntPtr psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(IntPtr psa, uint nDim, out int lBound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(IntPtr psa, uint nDim, out int uBound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(IntPtr psa, out IntPtr ppvData);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(IntPtr psa);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(ref Variant variant);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr parray;
    }
}
