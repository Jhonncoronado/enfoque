using System.Runtime.InteropServices;
using System.IO;

namespace Enfoque;

internal static class WindowsThemeManager
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint SpiSetHighContrast = 0x0043;
    private const uint HighContrastOn = 0x00000001;
    private const uint SpiSendChange = 0x0002;
    private static bool _changed;
    private static uint _previousFlags;
    private static string? _previousScheme;

    public static void EnableDuskTheme()
    {
        if (!_changed)
        {
            var current = new HighContrast
            {
                Size = (uint)Marshal.SizeOf<HighContrast>()
            };
            if (!SystemParametersInfo(SpiGetHighContrast, current.Size,
                    ref current, 0))
                return;

            _previousFlags = current.Flags;
            _previousScheme = current.DefaultScheme == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(current.DefaultScheme);
            _changed = true;
        }

        var schemePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Resources", "Ease of Access Themes", "hcblack.theme");
        var schemePointer = Marshal.StringToHGlobalUni(schemePath);
        try
        {
            var dusk = new HighContrast
            {
                Size = (uint)Marshal.SizeOf<HighContrast>(),
                Flags = _previousFlags | HighContrastOn,
                DefaultScheme = schemePointer
            };
            SystemParametersInfo(SpiSetHighContrast, dusk.Size, ref dusk,
                SpiSendChange);
        }
        finally
        {
            Marshal.FreeHGlobal(schemePointer);
        }
    }

    public static void RestorePreviousTheme()
    {
        if (!_changed) return;

        var schemePointer = _previousScheme is null
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(_previousScheme);
        try
        {
            var previous = new HighContrast
            {
                Size = (uint)Marshal.SizeOf<HighContrast>(),
                Flags = _previousFlags,
                DefaultScheme = schemePointer
            };
            SystemParametersInfo(SpiSetHighContrast, previous.Size,
                ref previous, SpiSendChange);
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
                Marshal.FreeHGlobal(schemePointer);
            _changed = false;
            _previousScheme = null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param,
        ref HighContrast value, uint updateFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }
}
