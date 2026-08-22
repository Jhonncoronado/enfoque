using System.Runtime.InteropServices;
using System.Windows;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Enfoque;

/// <summary>
/// Capa superior que oscurece el monitor y deja visibles las áreas enfocadas.
/// También administra el seguimiento de ventanas y el resaltado manual de
/// texto mediante Windows UI Automation.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private readonly System.Drawing.Rectangle _monitorBounds;
    private readonly double _scale;
    private readonly IntPtr _originalWindow;
    private List<FocusArea> _areas;
    private double _darkness;
    private readonly ControlPanelWindow _controlPanel;
    private readonly DispatcherTimer _followTimer;
    private readonly DispatcherTimer _taskManagerTimer;
    private readonly DispatcherTimer _obfuscationTimer;
    private readonly RelatedWindowTracker? _relatedTracker = null;
    private bool _followMouse;
    private bool _isPaused;
    private FocusShape _followShape = FocusShape.Rectangle;
    private double _followSize = 280;
    private IntPtr _trackedWindow;
    private List<FocusArea> _textAreas = [];
    private TextHighlightOptions _textOptions = new(false, string.Empty);
    private ColorInversionMode _colorInversionMode = ColorInversionMode.None;
    private readonly List<MagnifierWindow> _inversionHosts = [];
    private bool _pausedByTaskManager;
    private bool _wasPausedBeforeTaskManager;
    private bool _windowsColorFilterChanged;
    private object? _previousFilterActive;
    private object? _previousFilterType;
    private object? _previousAccessibilityConfiguration;
    private bool _obfuscateBackground;
    private string? _obfuscationMediaPath;
    private bool _obfuscationMediaIsVideo;
    private bool _showMediaPlain;
    private bool _nightThemeActive;
    private System.Windows.Media.Color _darknessColor =
        System.Windows.Media.Colors.Black;

    public event Action? AddAreaRequested;
    public event Action<int>? EditAreaRequested;
    public event Action? StopRequested;
    public System.Drawing.Rectangle MonitorBounds => _monitorBounds;
    public double Scale => _scale;
    public IReadOnlyList<FocusArea> Areas => _areas;
    public FocusShape SelectionShape => _controlPanel.SelectedShape;

    public OverlayWindow(IReadOnlyList<FocusArea> areas,
        System.Drawing.Rectangle monitorBounds, double scale, double darkness,
        string? relatedProcessName, IntPtr originalWindow, bool trackRelatedWindows,
        bool captureAnyPopup)
    {
        InitializeComponent();
        _monitorBounds = monitorBounds;
        _scale = scale;
        _originalWindow = originalWindow;
        _areas = areas.ToList();
        _darkness = darkness;
        Left = monitorBounds.Left / scale;
        Top = monitorBounds.Top / scale;
        Width = monitorBounds.Width / scale;
        Height = monitorBounds.Height / scale;
        PauseMask.Width = Width;
        PauseMask.Height = Height;
        ObfuscationImage.Width = Width;
        ObfuscationImage.Height = Height;
        ObfuscationVideo.Width = Width;
        ObfuscationVideo.Height = Height;

        _controlPanel = new ControlPanelWindow(_monitorBounds, _scale, _areas, _darkness);
        _controlPanel.AddAreaRequested += () => AddAreaRequested?.Invoke();
        _controlPanel.DarknessChanged += SetDarkness;
        _controlPanel.DarknessColorChanged += SetDarknessColor;
        _controlPanel.NightThemeChanged += SetNightTheme;
        _controlPanel.ObfuscationChanged += SetObfuscation;
        _controlPanel.ObfuscationMediaSelected += SetObfuscationMedia;
        _controlPanel.PlainMediaChanged += SetMediaPlainMode;
        _controlPanel.FollowMouseChanged += SetFollowMouse;
        _controlPanel.FollowShapeChanged += SetFollowShape;
        _controlPanel.FollowSizeChanged += SetFollowSize;
        _controlPanel.EditAreaRequested += index => EditAreaRequested?.Invoke(index);
        _controlPanel.RemoveAreaRequested += RemoveArea;
        _controlPanel.ClearAreasRequested += ClearAreas;
        _controlPanel.PauseChanged += SetPaused;
        _controlPanel.StopRequested += () => StopRequested?.Invoke();
        _controlPanel.TextHighlightOptionsChanged += SetTextHighlightOptions;
        Closed += OverlayWindow_Closed;

        _followTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _followTimer.Tick += (_, _) => BuildMask();

        _taskManagerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _taskManagerTimer.Tick += (_, _) => UpdateTaskManagerPauseState();
        _taskManagerTimer.Start();

        _obfuscationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _obfuscationTimer.Tick += (_, _) => CaptureObfuscatedBackground();

        if (trackRelatedWindows && !string.IsNullOrWhiteSpace(relatedProcessName))
        {
            _relatedTracker = new RelatedWindowTracker(
                relatedProcessName, originalWindow, captureAnyPopup);
            _relatedTracker.WindowShown += RelatedTracker_WindowShown;
        }

        BuildMask();

        SourceInitialized += OverlayWindow_SourceInitialized;
    }

    public void ShowControlPanel() => _controlPanel.Show();

    public void HideOverlay()
    {
        _controlPanel.Hide();
        Hide();
    }

    public void ShowOverlay()
    {
        Show();
        _controlPanel.Show();
    }

    public void UpdateAreas(IReadOnlyList<FocusArea> areas)
    {
        _areas = areas.ToList();
        BuildMask();
        _controlPanel.RefreshAreas(_areas);
    }

    private void SetDarkness(double darkness)
    {
        _darkness = darkness;
        ApplyMaskStyle();
        PauseMask.Opacity = darkness;
    }

    private void SetDarknessColor(System.Windows.Media.Color color)
    {
        _darknessColor = color;
        ApplyMaskStyle();
    }

    private void SetNightTheme(bool enabled)
    {
        _nightThemeActive = enabled;
        if (enabled) WindowsThemeManager.EnableDuskTheme();
        else WindowsThemeManager.RestorePreviousTheme();
    }

    private void SetObfuscation(bool enabled)
    {
        _obfuscateBackground = enabled;
        _obfuscationTimer.Stop();
        if (!enabled)
        {
            _obfuscationMediaPath = null;
            _obfuscationMediaIsVideo = false;
            _showMediaPlain = false;
            ObfuscationImage.Source = null;
            ObfuscationImage.Effect = null;
            ObfuscationVideo.Stop();
            ObfuscationVideo.Source = null;
            _controlPanel.SetObfuscationMediaLoadedState(false);
        }
        _controlPanel.SetMediaSelectionEnabled(enabled);
        if (enabled && string.IsNullOrWhiteSpace(_obfuscationMediaPath))
        {
            // La captura se hace una sola vez para que no haya parpadeo ni movimiento.
            CaptureObfuscatedBackground();
        }
        ApplyMaskStyle();
    }

    private void SetObfuscationMedia(string path, bool isVideo)
    {
        _obfuscationMediaPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _obfuscationMediaIsVideo = isVideo && _obfuscationMediaPath is not null;

        _obfuscationTimer.Stop();
        ObfuscationImage.Source = null;
        ObfuscationImage.Effect = null;
        ObfuscationVideo.Stop();
        ObfuscationVideo.Source = null;

        if (_obfuscationMediaPath is null)
        {
            _showMediaPlain = false;
            _controlPanel.SetObfuscationMediaLoadedState(false);
            if (_obfuscateBackground) CaptureObfuscatedBackground();
            ApplyMaskStyle();
            return;
        }

        try
        {
            if (_obfuscationMediaIsVideo)
            {
                ObfuscationVideo.Source = new Uri(_obfuscationMediaPath,
                    UriKind.Absolute);
                ObfuscationVideo.Position = TimeSpan.Zero;
                ObfuscationVideo.Play();
            }
            else
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(_obfuscationMediaPath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                ObfuscationImage.Source = image;
            }
            _controlPanel.SetObfuscationMediaLoadedState(true);
        }
        catch (Exception)
        {
            _obfuscationMediaPath = null;
            _obfuscationMediaIsVideo = false;
            _controlPanel.SetObfuscationMediaLoadedState(false);
        }

        ApplyMaskStyle();
    }

    private void ObfuscationVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_obfuscationMediaIsVideo || !_obfuscateBackground || _isPaused) return;
        ObfuscationVideo.Position = TimeSpan.Zero;
        ObfuscationVideo.Play();
    }

    private void SetMediaPlainMode(bool enabled)
    {
        _showMediaPlain = enabled && !string.IsNullOrWhiteSpace(_obfuscationMediaPath);
        ApplyMaskStyle();
    }

    private void ApplyMaskStyle()
    {
        var hasMedia = _obfuscateBackground &&
            !string.IsNullOrWhiteSpace(_obfuscationMediaPath);

        MaskPath.Fill = _obfuscateBackground
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)Math.Clamp(_darkness * 35, 10, 45), 180, 195, 215))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)(_darkness * 255), _darknessColor.R,
                _darknessColor.G, _darknessColor.B));
        MaskPath.Effect = null;
        MaskPath.Visibility = Visibility.Visible;

        if (hasMedia)
        {
            var mediaEffect = _showMediaPlain ? null : new BlurEffect
            {
                Radius = 18,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
            ObfuscationImage.Effect = mediaEffect;
            ObfuscationVideo.Effect = mediaEffect;
        }
        ObfuscationImage.Visibility = _obfuscateBackground && !_isPaused &&
            !_obfuscationMediaIsVideo && ObfuscationImage.Source is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ObfuscationVideo.Visibility = _obfuscateBackground && !_isPaused &&
            _obfuscationMediaIsVideo && ObfuscationVideo.Source is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ObfuscationImage.Clip = _obfuscateBackground
            ? MaskPath.Data?.Clone()
            : null;
        ObfuscationVideo.Clip = _obfuscateBackground
            ? MaskPath.Data?.Clone()
            : null;
    }

    private async void CaptureObfuscatedBackground()
    {
        if (!_obfuscateBackground || _isPaused || !IsVisible ||
            !string.IsNullOrWhiteSpace(_obfuscationMediaPath)) return;

        var overlayWasVisible = IsVisible;
        var panelWasVisible = _controlPanel.IsVisible;
        Hide();
        _controlPanel.Hide();

        try
        {
            await Task.Delay(35);
            using var bitmap = new Bitmap(_monitorBounds.Width,
                _monitorBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(_monitorBounds.Left, _monitorBounds.Top,
                    0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle,
                    IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            ObfuscationImage.Source = source;
                ObfuscationImage.Effect = new BlurEffect
                {
                    Radius = 18,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality
                };
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (ExternalException) { }
        finally
        {
            if (overlayWasVisible) Show();
            if (panelWasVisible) _controlPanel.Show();
            ApplyMaskStyle();
        }
    }

    private void SetTextHighlightOptions(TextHighlightOptions options)
    {
        _textOptions = options;

        if (options.SpecificTextEnabled)
            SearchTextAreas();
        else
            ClearTextAreas();
    }

    private void ClearTextAreas()
    {
        if (_textAreas.Count == 0) return;
        _textAreas.Clear();
        BuildMask();
    }

    private void SearchTextAreas()
    {
        if (_isPaused) return;

        var targets = new List<string>();
        if (_textOptions.SpecificTextEnabled &&
            !string.IsNullOrWhiteSpace(_textOptions.SpecificText))
            targets.Add(_textOptions.SpecificText.Trim());

        var newTextAreas = FindTextAreas(targets, scanDesktop: true);
        if (!AreAreasEqual(_textAreas, newTextAreas))
        {
            _textAreas = newTextAreas;
            BuildMask();
        }
    }

    private List<FocusArea> FindTextAreas(IReadOnlyList<string> targets, bool scanDesktop)
    {
        var result = new List<FocusArea>();
        if (targets.Count == 0) return result;

        var handles = new List<IntPtr>();
        if (_trackedWindow != IntPtr.Zero) handles.Add(_trackedWindow);
        handles.Add(_originalWindow);

        foreach (var handle in handles.Distinct())
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) continue;
            try
            {
                var root = AutomationElement.FromHandle(handle);
                if (root is not null) VisitAutomationElement(root, targets, result, 0);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        if (scanDesktop)
        {
            try
            {
                var desktopWindows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                foreach (AutomationElement window in desktopWindows)
                {
                    if (IntersectsSelectedMonitor(window))
                        VisitAutomationElement(window, targets, result, 0);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        return result
            .GroupBy(area => new { area.Bounds.X, area.Bounds.Y,
                area.Bounds.Width, area.Bounds.Height })
            .Select(group => group.First())
            .ToList();
    }

    private bool IntersectsSelectedMonitor(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return System.Drawing.Rectangle.Intersect(
                _monitorBounds,
                new System.Drawing.Rectangle((int)bounds.Left, (int)bounds.Top,
                    (int)bounds.Width, (int)bounds.Height)).Width > 0;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool AreAreasEqual(IReadOnlyList<FocusArea> first,
        IReadOnlyList<FocusArea> second)
    {
        if (first.Count != second.Count) return false;
        return first.OrderBy(area => area.Bounds.X).ThenBy(area => area.Bounds.Y)
            .Zip(second.OrderBy(area => area.Bounds.X).ThenBy(area => area.Bounds.Y))
            .All(pair => pair.First.Bounds == pair.Second.Bounds &&
                pair.First.Shape == pair.Second.Shape);
    }

    private void VisitAutomationElement(AutomationElement element,
        IReadOnlyList<string> targets, List<FocusArea> result, int depth)
    {
        if (depth > 128) return;

        try
        {
            // Solo se agregan los rectángulos exactos que devuelve TextPattern.
            // No usamos el BoundingRectangle del control como respaldo porque
            // eso puede resaltar todo el contenedor en lugar de la palabra.
            var textPatternMatch = TryAddTextPatternAreas(element, targets, result);
            if (!textPatternMatch)
                TryAddExactTextElementArea(element, targets, result);
        }
        catch (ElementNotAvailableException) { return; }

        AutomationElement? child = null;
        try
        {
            var walker = TreeWalker.RawViewWalker;
            child = walker.GetFirstChild(element);
            while (child is not null)
            {
                VisitAutomationElement(child, targets, result, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
    }

    private bool TryAddTextPatternAreas(AutomationElement element,
        IReadOnlyList<string> targets, List<FocusArea> result)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
                return false;

            var pattern = (TextPattern)patternObject;
            var found = false;
            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target)) continue;

                // Buscar todas las coincidencias. La primera puede estar fuera
                // de la parte visible del documento.
                var searchRange = pattern.DocumentRange.Clone();
                for (var occurrence = 0; occurrence < 100; occurrence++)
                {
                    TextPatternRange? range;
                    try
                    {
                        range = searchRange.FindText(target, false, true);
                    }
                    catch (COMException)
                    {
                        // Algunos proveedores UI Automation, como Firefox,
                        // no implementan FindText para todos sus documentos.
                        break;
                    }
                    catch (ArgumentException)
                    {
                        break;
                    }
                    catch (NotSupportedException)
                    {
                        if (TryAddTextByDocumentText(pattern, target, result))
                            found = true;
                        break;
                    }

                    if (range is null) break;

                    found = true;
                    foreach (var rectangle in range.GetBoundingRectangles())
                    {
                        var screenArea = System.Drawing.Rectangle.Intersect(
                            _monitorBounds,
                            new System.Drawing.Rectangle(
                                (int)rectangle.Left, (int)rectangle.Top,
                                (int)rectangle.Width, (int)rectangle.Height));
                        if (screenArea.Width >= 2 && screenArea.Height >= 2)
                            result.Add(new FocusArea(new System.Windows.Int32Rect(
                                screenArea.X, screenArea.Y,
                                screenArea.Width, screenArea.Height),
                                FocusShape.Rectangle));
                    }

                    // Continuar después del texto encontrado para no repetirlo.
                    try
                    {
                        searchRange.MoveEndpointByRange(
                            TextPatternRangeEndpoint.Start,
                            range,
                            TextPatternRangeEndpoint.End);
                    }
                    catch (COMException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            }

            return found;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (COMException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private bool TryAddTextByDocumentText(TextPattern pattern, string target,
        List<FocusArea> result)
    {
        try
        {
            var documentRange = pattern.DocumentRange;
            var documentText = documentRange.GetText(-1);
            if (string.IsNullOrEmpty(documentText)) return false;

            var found = false;
            var searchStart = 0;
            while (searchStart < documentText.Length)
            {
                var index = documentText.IndexOf(target, searchStart,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;

                var range = documentRange.Clone();
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,
                    TextUnit.Character, index);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End,
                    TextUnit.Character,
                    -(documentText.Length - index - target.Length));

                foreach (var rectangle in range.GetBoundingRectangles())
                {
                    var screenArea = System.Drawing.Rectangle.Intersect(
                        _monitorBounds,
                        new System.Drawing.Rectangle(
                            (int)rectangle.Left, (int)rectangle.Top,
                            (int)rectangle.Width, (int)rectangle.Height));
                    if (screenArea.Width >= 2 && screenArea.Height >= 2)
                    {
                        result.Add(new FocusArea(new System.Windows.Int32Rect(
                            screenArea.X, screenArea.Y,
                            screenArea.Width, screenArea.Height),
                            FocusShape.Rectangle));
                        found = true;
                    }
                }

                searchStart = index + Math.Max(target.Length, 1);
            }

            return found;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (COMException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private bool TryAddExactTextElementArea(AutomationElement element,
        IReadOnlyList<string> targets, List<FocusArea> result)
    {
        try
        {
            var controlType = element.Current.ControlType;
            if (controlType != ControlType.Text && controlType != ControlType.Hyperlink)
                return false;

            var name = element.Current.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                !targets.Any(target => string.Equals(name, target.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
                return false;

            var bounds = element.Current.BoundingRectangle;
            var screenArea = System.Drawing.Rectangle.Intersect(
                _monitorBounds,
                new System.Drawing.Rectangle(
                    (int)bounds.Left, (int)bounds.Top,
                    (int)bounds.Width, (int)bounds.Height));

            // Solo usar elementos pequeños que representan el texto mismo.
            // Se descartan paneles o contenedores grandes.
            if (screenArea.Width < 2 || screenArea.Height < 2 ||
                screenArea.Width > _monitorBounds.Width * 0.5 ||
                screenArea.Height > _monitorBounds.Height * 0.25)
                return false;

            result.Add(new FocusArea(new System.Windows.Int32Rect(
                screenArea.X, screenArea.Y, screenArea.Width, screenArea.Height),
                FocusShape.Rectangle));
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void SetFollowMouse(bool enabled)
    {
        if (_isPaused) return;
        _followMouse = enabled;
        if (enabled) _followTimer.Start();
        else
        {
            _followTimer.Stop();
            BuildMask();
        }
    }

    private void RelatedTracker_WindowShown(IntPtr window)
    {
        if (_isPaused) return;
        Dispatcher.BeginInvoke(() => SetTrackedWindow(window));
    }

    private void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (paused)
        {
            _obfuscationTimer.Stop();
            ObfuscationImage.Visibility = Visibility.Collapsed;
            ObfuscationVideo.Stop();
            if (_nightThemeActive)
                WindowsThemeManager.RestorePreviousTheme();
        }
        else if (_nightThemeActive)
        {
            WindowsThemeManager.EnableDuskTheme();
        }

        if (!paused && _obfuscationMediaIsVideo &&
            ObfuscationVideo.Source is not null)
            ObfuscationVideo.Play();

        if (paused)
        {
            _followTimer.Stop();
            // Pausar quita completamente el sombreado, pero conserva visible
            // el panel lateral para poder pulsar Reanudar.
            MaskPath.Visibility = Visibility.Collapsed;
            PauseMask.Visibility = Visibility.Collapsed;
        }
        else if (_followMouse || _trackedWindow != IntPtr.Zero)
        {
            _followTimer.Start();
            BuildMask();
        }
        else
        {
            BuildMask();
        }

        UpdateColorInversion();
        if (!paused && _obfuscateBackground)
            ApplyMaskStyle();
    }

    private void UpdateWindowsColorFilter()
    {
        if (_colorInversionMode != ColorInversionMode.FullScreen)
        {
            RestoreWindowsColorFilter();
            return;
        }

        try
        {
            const string colorFilteringPath = @"Software\Microsoft\ColorFiltering";
            const string accessibilityPath =
                @"Software\Microsoft\Windows NT\CurrentVersion\Accessibility";

            using var colorFiltering = Registry.CurrentUser.CreateSubKey(
                colorFilteringPath, writable: true);
            if (colorFiltering is null) return;

            if (!_windowsColorFilterChanged)
            {
                _previousFilterActive = colorFiltering.GetValue("Active");
                _previousFilterType = colorFiltering.GetValue("FilterType");
                using var accessibility = Registry.CurrentUser.OpenSubKey(
                    accessibilityPath, writable: false);
                _previousAccessibilityConfiguration = accessibility?.GetValue(
                    "Configuration");
                _windowsColorFilterChanged = true;
            }

            colorFiltering.SetValue("FilterType", 1, RegistryValueKind.DWord);
            colorFiltering.SetValue("Active", _isPaused ? 0 : 1,
                RegistryValueKind.DWord);

            using var accessibilityWrite = Registry.CurrentUser.CreateSubKey(
                accessibilityPath, writable: true);
            accessibilityWrite?.SetValue("Configuration", "colorfiltering",
                RegistryValueKind.String);
            BroadcastColorFilterChange();
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RestoreWindowsColorFilter()
    {
        if (!_windowsColorFilterChanged) return;

        try
        {
            const string colorFilteringPath = @"Software\Microsoft\ColorFiltering";
            const string accessibilityPath =
                @"Software\Microsoft\Windows NT\CurrentVersion\Accessibility";

            using (var colorFiltering = Registry.CurrentUser.CreateSubKey(
                colorFilteringPath, writable: true))
            {
                RestoreRegistryValue(colorFiltering, "Active", _previousFilterActive);
                RestoreRegistryValue(colorFiltering, "FilterType", _previousFilterType);
            }

            using (var accessibility = Registry.CurrentUser.CreateSubKey(
                accessibilityPath, writable: true))
            {
                RestoreRegistryValue(accessibility, "Configuration",
                    _previousAccessibilityConfiguration);
            }

            BroadcastColorFilterChange();
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            _windowsColorFilterChanged = false;
            _previousFilterActive = null;
            _previousFilterType = null;
            _previousAccessibilityConfiguration = null;
        }
    }

    private static void RestoreRegistryValue(RegistryKey? key, string name,
        object? value)
    {
        if (key is null) return;
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value);
    }

    private static void BroadcastColorFilterChange()
    {
        SendMessageTimeout(new IntPtr(0xffff), WmSettingChange, IntPtr.Zero,
            "ColorFiltering", SendMessageTimeoutFlags, 2000, out _);
    }

    private void UpdateTaskManagerPauseState()
    {
        var taskManagerOpen = false;
        try
        {
            taskManagerOpen = Process.GetProcessesByName("Taskmgr").Length > 0;
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        if (taskManagerOpen && !_pausedByTaskManager)
        {
            _pausedByTaskManager = true;
            _wasPausedBeforeTaskManager = _isPaused;
            if (!_isPaused)
            {
                _controlPanel.SetPausedState(true);
                SetPaused(true);
            }
        }
        else if (!taskManagerOpen && _pausedByTaskManager)
        {
            _pausedByTaskManager = false;
            if (!_wasPausedBeforeTaskManager)
            {
                _controlPanel.SetPausedState(false);
                SetPaused(false);
            }
        }
    }

    private void SetTrackedWindow(IntPtr window)
    {
        if (_isPaused) return;
        if (!IsWindow(window) || !IsWindowVisible(window) ||
            !GetWindowRect(window, out var rect)) return;

        var intersection = System.Drawing.Rectangle.Intersect(
            _monitorBounds,
            new System.Drawing.Rectangle(rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top));
        if (intersection.Width <= 0 || intersection.Height <= 0) return;

        _trackedWindow = window;
        _followTimer.Start();
        BuildMask();
    }

    private void SetFollowShape(FocusShape shape)
    {
        _followShape = shape;
        if (_followMouse) BuildMask();
    }

    private void SetFollowSize(double size)
    {
        _followSize = size;
        if (_followMouse) BuildMask();
    }

    private void RemoveArea(int index)
    {
        if (index < 0 || index >= _areas.Count) return;
        _areas.RemoveAt(index);
        BuildMask();
        _controlPanel.RefreshAreas(_areas);
    }

    private void ClearAreas()
    {
        _areas.Clear();
        BuildMask();
        _controlPanel.RefreshAreas(_areas);
    }

    private void BuildMask()
    {
        var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));

        if (_isPaused)
        {
            // Mientras está pausado no se muestra ninguna capa.
            MaskPath.Visibility = Visibility.Collapsed;
            PauseMask.Visibility = Visibility.Collapsed;
            return;
        }

        MaskPath.Visibility = Visibility.Visible;
        PauseMask.Visibility = Visibility.Collapsed;

        var innerGroup = new GeometryGroup { FillRule = FillRule.Nonzero };

        if (!_isPaused && _trackedWindow != IntPtr.Zero &&
            IsWindowVisible(_trackedWindow) && GetWindowRect(_trackedWindow, out var trackedRect))
        {
            var trackedBounds = System.Drawing.Rectangle.Intersect(
                _monitorBounds,
                new System.Drawing.Rectangle(trackedRect.Left, trackedRect.Top,
                    trackedRect.Right - trackedRect.Left, trackedRect.Bottom - trackedRect.Top));
            if (trackedBounds.Width > 0 && trackedBounds.Height > 0)
            {
                var trackedFocus = new Rect(
                    (trackedBounds.X - _monitorBounds.Left) / _scale,
                    (trackedBounds.Y - _monitorBounds.Top) / _scale,
                    trackedBounds.Width / _scale,
                    trackedBounds.Height / _scale);
                innerGroup.Children.Add(new RectangleGeometry(trackedFocus));
            }
        }
        else if (!_isPaused && _followMouse && GetCursorPos(out var cursor) &&
            _monitorBounds.Contains(cursor.X, cursor.Y))
        {
            var size = Math.Min(_followSize, Math.Min(_monitorBounds.Width, _monitorBounds.Height));
            var width = Math.Min(
                _followShape == FocusShape.Rectangle ? size * 1.35 : size,
                _monitorBounds.Width);
            var height = Math.Min(size, _monitorBounds.Height);
            var left = cursor.X - width / 2;
            var top = cursor.Y - height / 2;
            left = Math.Clamp(left, _monitorBounds.Left, _monitorBounds.Right - width);
            top = Math.Clamp(top, _monitorBounds.Top, _monitorBounds.Bottom - height);

            var followRect = new Rect(
                (left - _monitorBounds.Left) / _scale,
                (top - _monitorBounds.Top) / _scale,
                width / _scale,
                height / _scale);
            innerGroup.Children.Add(_followShape == FocusShape.Circle
                ? new EllipseGeometry(followRect)
                : new RectangleGeometry(followRect));
        }
        else if (!_isPaused) foreach (var area in _areas)
        {
            var focusRect = new Rect(
                (area.Bounds.X - _monitorBounds.Left) / _scale,
                (area.Bounds.Y - _monitorBounds.Top) / _scale,
                area.Bounds.Width / _scale,
                area.Bounds.Height / _scale);

            Geometry inner = area.Shape == FocusShape.Circle
                ? new EllipseGeometry(focusRect)
                : new RectangleGeometry(focusRect);
            innerGroup.Children.Add(inner);
        }

        foreach (var area in _textAreas)
        {
            var textRect = new Rect(
                (area.Bounds.X - _monitorBounds.Left) / _scale,
                (area.Bounds.Y - _monitorBounds.Top) / _scale,
                area.Bounds.Width / _scale,
                area.Bounds.Height / _scale);
            innerGroup.Children.Add(new RectangleGeometry(textRect));
        }

        MaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, innerGroup);
        ApplyMaskStyle();
        UpdateColorInversion();
    }

    private void UpdateColorInversion()
    {
        if (_colorInversionMode == ColorInversionMode.FullScreen)
        {
            ClearInversionHosts();
            UpdateWindowsColorFilter();
            return;
        }

        RestoreWindowsColorFilter();

        if (_isPaused || _colorInversionMode == ColorInversionMode.None)
        {
            ClearInversionHosts();
            return;
        }

        var regions = GetCurrentFocusRegions();
        var desired = new List<(System.Drawing.Rectangle Bounds, bool Invert)>();

        if (_colorInversionMode == ColorInversionMode.FullScreen)
        {
            desired.Add((_monitorBounds, true));
        }
        else if (_colorInversionMode == ColorInversionMode.FocusedAreas)
        {
            desired.AddRange(regions.Select(region => (region, true)));
        }
        else
        {
            desired.Add((_monitorBounds, true));
            desired.AddRange(regions.Select(region => (region, false)));
        }

        desired = desired
            .Where(item => item.Bounds.Width > 1 && item.Bounds.Height > 1)
            .ToList();

        try
        {
            var excludedWindows = new[]
            {
                new WindowInteropHelper(this).Handle,
                new WindowInteropHelper(_controlPanel).Handle
            };

            while (_inversionHosts.Count < desired.Count)
            {
                var item = desired[_inversionHosts.Count];
                _inversionHosts.Add(new MagnifierWindow(
                    item.Bounds, item.Invert, excludedWindows));
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var item = desired[index];
                if (_inversionHosts[index].Inverted != item.Invert)
                {
                    var replacement = new MagnifierWindow(
                        item.Bounds, item.Invert, excludedWindows);
                    _inversionHosts[index].Dispose();
                    _inversionHosts[index] = replacement;
                }
                else
                {
                    _inversionHosts[index].Update(item.Bounds, item.Invert);
                }
            }

            while (_inversionHosts.Count > desired.Count)
            {
                var lastIndex = _inversionHosts.Count - 1;
                _inversionHosts[lastIndex].Dispose();
                _inversionHosts.RemoveAt(lastIndex);
            }

            _controlPanel.BringToFrontWithoutFocus();
        }
        catch (DllNotFoundException)
        {
            ClearInversionHosts();
        }
        catch (InvalidOperationException)
        {
            ClearInversionHosts();
        }
    }

    private List<System.Drawing.Rectangle> GetCurrentFocusRegions()
    {
        var regions = new List<System.Drawing.Rectangle>();

        if (_trackedWindow != IntPtr.Zero &&
            IsWindowVisible(_trackedWindow) && GetWindowRect(_trackedWindow, out var trackedRect))
        {
            AddMonitorIntersection(regions, new System.Drawing.Rectangle(
                trackedRect.Left, trackedRect.Top,
                trackedRect.Right - trackedRect.Left,
                trackedRect.Bottom - trackedRect.Top));
        }
        else if (_followMouse && GetCursorPos(out var cursor) &&
                 _monitorBounds.Contains(cursor.X, cursor.Y))
        {
            var size = Math.Min(_followSize, Math.Min(_monitorBounds.Width, _monitorBounds.Height));
            var width = Math.Min(
                _followShape == FocusShape.Rectangle ? size * 1.35 : size,
                _monitorBounds.Width);
            var height = Math.Min(size, _monitorBounds.Height);
            var left = Math.Clamp(cursor.X - width / 2, _monitorBounds.Left,
                _monitorBounds.Right - width);
            var top = Math.Clamp(cursor.Y - height / 2, _monitorBounds.Top,
                _monitorBounds.Bottom - height);
            regions.Add(new System.Drawing.Rectangle(
                (int)left, (int)top, (int)width, (int)height));
        }
        else
        {
            foreach (var area in _areas)
                AddMonitorIntersection(regions, new System.Drawing.Rectangle(
                    area.Bounds.X, area.Bounds.Y,
                    area.Bounds.Width, area.Bounds.Height));
        }

        return regions
            .GroupBy(region => new { region.X, region.Y, region.Width, region.Height })
            .Select(group => group.First())
            .ToList();
    }

    private void AddMonitorIntersection(List<System.Drawing.Rectangle> regions,
        System.Drawing.Rectangle bounds)
    {
        var intersection = System.Drawing.Rectangle.Intersect(_monitorBounds, bounds);
        if (intersection.Width > 1 && intersection.Height > 1)
            regions.Add(intersection);
    }

    private void ClearInversionHosts()
    {
        foreach (var host in _inversionHosts)
        {
            host.Dispose();
        }
        _inversionHosts.Clear();
    }

    private void OverlayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle,
            new IntPtr(style | WsExTransparent | WsExNoActivate | WsExToolWindow));

        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        SetWindowPos(handle, HwndTopmost, _monitorBounds.Left, _monitorBounds.Top,
            _monitorBounds.Width, _monitorBounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        _followTimer.Stop();
        _taskManagerTimer.Stop();
        _obfuscationTimer.Stop();
        if (_nightThemeActive)
            WindowsThemeManager.RestorePreviousTheme();
        RestoreWindowsColorFilter();
        ClearInversionHosts();
        _relatedTracker?.Dispose();
        _controlPanel.Close();
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmSettingChange = 0x001A;
    private const uint SendMessageTimeoutFlags = 0x0000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
        IntPtr wParam, string lParam, uint flags, uint timeout,
        out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
