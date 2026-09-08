using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace HonorHelper;

internal static class DisplayRefreshRate
{
    private const int EnumCurrentSettings = -1;
    private const int DispChangeSuccessful = 0;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsTest = 0x00000002;

    internal readonly record struct State(int CurrentRate, IReadOnlyList<int> AvailableRates);
    internal readonly record struct SetResult(bool Ok, string Error);

    internal static State? GetState()
    {
        var current = CreateDevMode();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref current))
            return null;

        var rates = new SortedSet<int>();
        for (int index = 0; ; index++)
        {
            var mode = CreateDevMode();
            if (!EnumDisplaySettings(null, index, ref mode))
                break;
            if (mode.dmPelsWidth == current.dmPelsWidth &&
                mode.dmPelsHeight == current.dmPelsHeight &&
                mode.dmBitsPerPel == current.dmBitsPerPel &&
                mode.dmDisplayOrientation == current.dmDisplayOrientation &&
                mode.dmDisplayFrequency > 1)
            {
                rates.Add((int)mode.dmDisplayFrequency);
            }
        }

        rates.Add((int)current.dmDisplayFrequency);
        return new State((int)current.dmDisplayFrequency, rates.ToArray());
    }

    internal static SetResult Set(int refreshRate)
    {
        var mode = CreateDevMode();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
            return new(false, "无法读取当前显示模式");

        mode.dmFields = DmDisplayFrequency;
        mode.dmDisplayFrequency = (uint)refreshRate;
        if (ChangeDisplaySettings(ref mode, CdsTest) != DispChangeSuccessful)
            return new(false, $"显示器不支持 {refreshRate} Hz");
        if (ChangeDisplaySettings(ref mode, 0) != DispChangeSuccessful)
            return new(false, $"切换到 {refreshRate} Hz 失败");

        return new(true, string.Empty);
    }

    private static DevMode CreateDevMode()
        => new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DevMode>(),
        };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DevMode devMode, uint flags);
}
