using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Enfoque;

/// <summary>
/// Ventana inicial: selecciona el monitor, captura la ventana objetivo y
/// coordina la ventana de selección con la capa de enfoque.
/// </summary>
public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int HotkeyId = 9000;
    private const int ExitHotkeyId = 9001;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkF = 0x46;
    private const uint VkX = 0x58;
    private const uint GwHwndPrev = 3;

    private OverlayWindow? _overlay;
    private SelectionWindow? _selectionWindow;
    private HwndSource? _source;
    private readonly List<MonitorOption> _monitors = [];
    private IntPtr _lastTargetWindow = IntPtr.Zero;

    public MainWindow()
    {
        InitializeComponent();
        LoadMonitors();
        SourceInitialized += MainWindow_SourceInitialized;
        Deactivated += MainWindow_Deactivated;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        // Cuando el usuario vuelve desde otra aplicación, guardamos esa ventana
        // para que el botón no intente enfocar la propia interfaz de Enfoque.
        Dispatcher.BeginInvoke(() =>
        {
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero && window != _source?.Handle)
                _lastTargetWindow = window;
        });
    }

    private void LoadMonitors()
    {
        _monitors.Clear();
        var screens = Forms.Screen.AllScreens;

        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            _monitors.Add(new MonitorOption(
                screen,
                $"Monitor {index + 1}{(screen.Primary ? " (principal)" : "")} — " +
                $"{screen.Bounds.Width} x {screen.Bounds.Height}"));
        }

        MonitorComboBox.ItemsSource = _monitors;
        MonitorComboBox.SelectedIndex = 0;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WndProc);
        RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModAlt, VkF);
        // Windows normalmente no expone Fn como una tecla independiente;
        // Ctrl+Fn+X llega como Ctrl+X al proceso.
        RegisterHotKey(_source.Handle, ExitHotkeyId, ModControl, VkX);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // Capturamos la ventana que el usuario desea enfocar antes de que
        // este botón convierta a Enfoque en la ventana activa.
        var targetWindow = _lastTargetWindow;
        if (targetWindow == IntPtr.Zero || targetWindow == _source?.Handle)
            targetWindow = GetPreviousWindow(_source?.Handle ?? IntPtr.Zero);
        Hide();

        // Damos tiempo a que la interfaz desaparezca antes de mostrar la capa.
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(180);
            // Al ocultar Enfoque, Windows suele devolver el foco a la aplicación
            // que estaba debajo. Esa es la fuente más fiable para este caso.
            var foreground = GetForegroundWindow();
            var target = IsValidTarget(foreground) ? foreground : targetWindow;
            if (target == IntPtr.Zero || target == _source?.Handle || !IsWindow(target))
            {
                Show();
                return;
            }

            if (!GetWindowRect(target, out var rect) || MonitorComboBox.SelectedItem is not MonitorOption monitor)
            {
                Show();
                return;
            }

            var selectedBounds = monitor.Screen.Bounds;
            var targetBounds = new System.Drawing.Rectangle(
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            var focusBounds = System.Drawing.Rectangle.Intersect(selectedBounds, targetBounds);

            _overlay = new OverlayWindow(
                [new FocusArea(new System.Windows.Int32Rect(
                    focusBounds.X, focusBounds.Y, focusBounds.Width, focusBounds.Height),
                    FocusShape.Rectangle)],
                selectedBounds,
                GetInterfaceScale(),
                OpacitySlider.Value,
                GetProcessName(target),
                target,
                TrackRelatedWindowsCheckBox.IsChecked == true,
                CaptureAnyPopupCheckBox.IsChecked == true);
            ConnectOverlay(_overlay);
            _overlay.Show();
            _overlay.ShowControlPanel();
            StartButton.IsEnabled = false;
            DrawAreaButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        });
    }

    private void DrawAreaButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorComboBox.SelectedItem is not MonitorOption monitor) return;

        var selection = new SelectionWindow(monitor.Screen.Bounds, GetSelectedShape(), GetInterfaceScale());
        _selectionWindow = selection;
        selection.SelectionCompleted += areas =>
        {
            selection.Close();
            _selectionWindow = null;

            if (areas is null || areas.Count == 0)
            {
                Show();
                Activate();
                return;
            }

            // Esperamos a que la ventana de selección termine de cerrarse
            // antes de crear las dos ventanas del modo enfoque.
            Dispatcher.BeginInvoke(() =>
            {
            _overlay = new OverlayWindow(areas, monitor.Screen.Bounds,
                GetInterfaceScale(), OpacitySlider.Value,
                GetProcessName(_lastTargetWindow),
                _lastTargetWindow,
                TrackRelatedWindowsCheckBox.IsChecked == true,
                CaptureAnyPopupCheckBox.IsChecked == true);
                ConnectOverlay(_overlay);
                _overlay.Show();
                _overlay.ShowControlPanel();
                StartButton.IsEnabled = false;
                DrawAreaButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            });
        };

        Hide();
        selection.Show();
        selection.Activate();
    }

    private FocusShape GetSelectedShape()
    {
        if (ShapeComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<FocusShape>(item.Tag?.ToString(), out var shape))
            return shape;

        return FocusShape.Rectangle;
    }

    private void ConnectOverlay(OverlayWindow overlay)
    {
        overlay.AddAreaRequested += Overlay_AddAreaRequested;
        overlay.EditAreaRequested += Overlay_EditAreaRequested;
        overlay.StopRequested += StopFocus;
    }

    private void Overlay_AddAreaRequested()
    {
        if (_overlay is null) return;

        var overlay = _overlay;
        overlay.HideOverlay();

        var selection = new SelectionWindow(
            overlay.MonitorBounds, overlay.SelectionShape, overlay.Scale, overlay.Areas);
        _selectionWindow = selection;
        selection.SelectionCompleted += areas =>
        {
            selection.Close();
            _selectionWindow = null;

            if (areas is not null)
                overlay.UpdateAreas(areas);

            overlay.ShowOverlay();
        };

        selection.Show();
        selection.Activate();
    }

    private void Overlay_EditAreaRequested(int index)
    {
        if (_overlay is null || index < 0 || index >= _overlay.Areas.Count) return;

        var overlay = _overlay;
        var remainingAreas = overlay.Areas.ToList();
        remainingAreas.RemoveAt(index);
        overlay.HideOverlay();

        var selection = new SelectionWindow(
            overlay.MonitorBounds, overlay.SelectionShape, overlay.Scale, remainingAreas);
        _selectionWindow = selection;
        selection.SelectionCompleted += areas =>
        {
            selection.Close();
            _selectionWindow = null;

            if (areas is not null)
                overlay.UpdateAreas(areas);

            overlay.ShowOverlay();
        };

        selection.Show();
        selection.Activate();
    }

    private double GetInterfaceScale()
    {
        return PresentationSource.FromVisual(this) is not null
            ? VisualTreeHelper.GetDpi(this).DpiScaleX
            : 1.0;
    }

    private static string? GetProcessName(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;

        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return null; }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopFocus();

    private void StopFocus()
    {
        _overlay?.Close();
        _overlay = null;
        _selectionWindow?.Close();
        _selectionWindow = null;
        StartButton.IsEnabled = true;
        DrawAreaButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        Show();
        Activate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            if (_overlay is null) StartButton_Click(this, new RoutedEventArgs());
            else StopFocus();
            handled = true;
        }
        else if (msg == WmHotKey && wParam.ToInt32() == ExitHotkeyId)
        {
            Close();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_source is not null) UnregisterHotKey(_source.Handle, HotkeyId);
        if (_source is not null) UnregisterHotKey(_source.Handle, ExitHotkeyId);
        _overlay?.Close();
        _selectionWindow?.Close();
    }

    private static IntPtr GetPreviousWindow(IntPtr ownHandle)
    {
        var candidate = GetWindow(ownHandle, GwHwndPrev);
        while (candidate != IntPtr.Zero)
        {
            if (candidate != ownHandle && IsWindow(candidate) && IsWindowVisible(candidate))
                return candidate;

            candidate = GetWindow(candidate, GwHwndPrev);
        }

        return IntPtr.Zero;
    }

    private bool IsValidTarget(IntPtr window)
    {
        return window != IntPtr.Zero && window != _source?.Handle && IsWindow(window);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record MonitorOption(Forms.Screen Screen, string DisplayName);
}
