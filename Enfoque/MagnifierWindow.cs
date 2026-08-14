using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Enfoque;

/// <summary>
/// Ventana nativa independiente que muestra una región del escritorio con
/// una transformación de color. Se mantiene fuera de la ventana transparente
/// de WPF para evitar que la captura aparezca negra.
/// </summary>
internal sealed class MagnifierWindow : IDisposable
{
    private const string MagnifierClass = "Magnifier";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoZOrder = 0x0004;
    private const int MagFilterExclude = 0;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly float[] IdentityColorMatrix =
    [
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1
    ];

    private static readonly float[] InversionColorMatrix =
    [
        -1, 0, 0, 0, 1,
        0, -1, 0, 0, 1,
        0, 0, -1, 0, 1,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1
    ];

    private static readonly float[] IdentityTransform =
    [
        1, 0, 0,
        0, 1, 0,
        0, 0, 1
    ];

    private static readonly float[] FullscreenInversionMatrix =
    [
        -1, 0, 0, 0, 1,
        0, -1, 0, 0, 1,
        0, 0, -1, 0, 1,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1
    ];

    private static bool _magnificationInitialized;
    private readonly HwndSource _sourceWindow;
    private readonly IntPtr _magnifierHandle;
    private readonly IntPtr[] _additionalExcludedWindows;
    private System.Drawing.Rectangle _sourceRect;
    private bool _invert;
    private bool _disposed;

    public bool Inverted => _invert;

    public static bool SetFullscreenInversion(bool enabled)
    {
        EnsureMagnificationInitialized();
        var effect = new MagColorEffect
        {
            Transform = (enabled ? FullscreenInversionMatrix : IdentityColorMatrix).ToArray()
        };
        return MagSetFullscreenColorEffect(ref effect);
    }

    public MagnifierWindow(System.Drawing.Rectangle sourceRect, bool invert,
        IEnumerable<IntPtr>? additionalExcludedWindows = null)
    {
        _sourceRect = sourceRect;
        _invert = invert;
        _additionalExcludedWindows = additionalExcludedWindows?
            .Where(handle => handle != IntPtr.Zero)
            .Distinct()
            .ToArray() ?? [];
        EnsureMagnificationInitialized();

        var parameters = new HwndSourceParameters("EnfoqueMagnifier")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExNoActivate | WsExToolWindow,
            Width = Math.Max(1, sourceRect.Width),
            Height = Math.Max(1, sourceRect.Height),
            PositionX = sourceRect.Left,
            PositionY = sourceRect.Top,
            UsesPerPixelOpacity = false
        };

        _sourceWindow = new HwndSource(parameters);
        _sourceWindow.AddHook(WindowProc);
        _magnifierHandle = CreateWindowEx(
            0,
            MagnifierClass,
            string.Empty,
            WsChild | WsVisible,
            0, 0, sourceRect.Width, sourceRect.Height,
            _sourceWindow.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_magnifierHandle == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo crear la ventana de magnificación.");

        ApplySettings();
    }

    public void Update(System.Drawing.Rectangle sourceRect, bool invert)
    {
        if (_disposed) return;
        _sourceRect = sourceRect;
        _invert = invert;
        SetWindowPos(_sourceWindow.Handle, HwndTopmost,
            sourceRect.Left, sourceRect.Top,
            Math.Max(1, sourceRect.Width), Math.Max(1, sourceRect.Height),
            SwpNoActivate | SwpShowWindow);
        SetWindowPos(_magnifierHandle, IntPtr.Zero,
            0, 0, Math.Max(1, sourceRect.Width), Math.Max(1, sourceRect.Height),
            SwpNoActivate | SwpNoZOrder);
        ApplySettings();
    }

    private void ApplySettings()
    {
        var source = new NativeRect(_sourceRect);
        MagSetWindowSource(_magnifierHandle, ref source);

        var transform = new MagTransform { Matrix = IdentityTransform.ToArray() };
        MagSetWindowTransform(_magnifierHandle, ref transform);

        // MS_INVERTCOLORS, aplicado al control al crearlo, realiza la
        // inversión cromática negativa. Aquí se deja la matriz identidad
        // para no duplicar ni cancelar ese efecto nativo.
        var effect = new MagColorEffect
        {
            Transform = (_invert ? InversionColorMatrix : IdentityColorMatrix).ToArray()
        };
        MagSetColorEffect(_magnifierHandle, ref effect);

        // Excluir tanto el contenedor como el control Magnifier evita que la
        // propia imagen ampliada se capture de nuevo y termine en negro.
        var excludedWindows = new[] { _sourceWindow.Handle, _magnifierHandle }
            .Concat(_additionalExcludedWindows)
            .Distinct()
            .ToArray();
        MagSetWindowFilterList(_magnifierHandle, MagFilterExclude,
            excludedWindows.Length, excludedWindows);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_magnifierHandle != IntPtr.Zero)
            DestroyWindow(_magnifierHandle);
        _sourceWindow.RemoveHook(WindowProc);
        _sourceWindow.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void EnsureMagnificationInitialized()
    {
        if (_magnificationInitialized) return;
        if (!MagInitialize())
            throw new InvalidOperationException("Windows Magnification no está disponible.");
        _magnificationInitialized = true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent,
        IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetWindowSource(IntPtr hwnd, ref NativeRect source);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetWindowTransform(IntPtr hwnd, ref MagTransform transform);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetColorEffect(IntPtr hwnd, ref MagColorEffect effect);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetFullscreenColorEffect(ref MagColorEffect effect);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetWindowFilterList(
        IntPtr hwnd, int mode, int count,
        [In] IntPtr[] filters);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(System.Drawing.Rectangle rectangle)
        {
            Left = rectangle.Left;
            Top = rectangle.Top;
            Right = rectangle.Right;
            Bottom = rectangle.Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MagTransform
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] Matrix;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MagColorEffect
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] Transform;
    }
}
