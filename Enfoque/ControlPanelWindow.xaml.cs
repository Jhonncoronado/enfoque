using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Enfoque;

/// <summary>
/// Panel lateral para configurar el sombreado, las áreas, el seguimiento y
/// el resaltado manual de texto.
/// </summary>
public partial class ControlPanelWindow : Window
{
    private readonly System.Drawing.Rectangle _monitorBounds;
    private readonly double _scale;
    private bool _expanded;
    private bool _isPaused;
    private double _topPixels;
    private readonly DispatcherTimer _edgeTimer;

    public event Action? AddAreaRequested;
    public event Action<double>? DarknessChanged;
    public event Action<bool>? FollowMouseChanged;
    public event Action<FocusShape>? FollowShapeChanged;
    public event Action<double>? FollowSizeChanged;
    public event Action<int>? EditAreaRequested;
    public event Action<int>? RemoveAreaRequested;
    public event Action? ClearAreasRequested;
    public event Action<bool>? PauseChanged;
    public event Action? StopRequested;
    public event Action<TextHighlightOptions>? TextHighlightOptionsChanged;

    public ControlPanelWindow(System.Drawing.Rectangle monitorBounds, double scale,
        IReadOnlyList<FocusArea> areas, double darkness)
    {
        InitializeComponent();
        _monitorBounds = monitorBounds;
        _scale = scale;
        _topPixels = monitorBounds.Top + (monitorBounds.Height - 52 * scale) / 2;
        DarknessSlider.Value = darkness;
        FollowShapeComboBox.SelectedIndex = 0;
        RefreshAreas(areas);
        PositionWindow();
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _edgeTimer.Tick += (_, _) => UpdateQuickBarVisibility();
        _edgeTimer.Start();
        Closed += (_, _) => _edgeTimer.Stop();
    }

    public FocusShape SelectedShape => FollowShapeComboBox.SelectedItem is ComboBoxItem item &&
        Enum.TryParse<FocusShape>(item.Tag?.ToString(), out var shape)
            ? shape : FocusShape.Rectangle;

    public void RefreshAreas(IReadOnlyList<FocusArea> areas)
    {
        AreasList.ItemsSource = areas
            .Select((area, index) => new AreaListItem(index, Describe(area)))
            .ToList();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        Width = _expanded ? 388 : 48;
        Height = _expanded ? 700 : 52;
        SettingsPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleButton.Content = _expanded ? "×" : "☰";
        ToggleButton.ToolTip = _expanded ? "Cerrar opciones" : "Abrir opciones";
        PositionWindow();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        PauseButton.Content = _isPaused ? "▶" : "⏸";
        PauseButton.ToolTip = _isPaused ? "Reanudar" : "Pausar";
        PauseChanged?.Invoke(_isPaused);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
        => StopRequested?.Invoke();

    private void DragHandle_MouseLeftButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            _topPixels = Top * _scale;
            _topPixels = Math.Clamp(_topPixels, _monitorBounds.Top,
                _monitorBounds.Bottom - Height * _scale);
            PositionWindow();
        }
        catch (InvalidOperationException) { }
        e.Handled = true;
    }

    private void ControlPanelWindow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => ShowQuickBar();

    private void UpdateQuickBarVisibility()
    {
        if (_expanded || IsMouseOver || IsCursorNearLeftEdge())
            ShowQuickBar();
        else
            HideQuickBar();
    }

    private void ShowQuickBar()
    {
        QuickBar.Visibility = Visibility.Visible;
        Width = _expanded ? 388 : 168;
    }

    private void HideQuickBar()
    {
        if (_expanded) return;
        QuickBar.Visibility = Visibility.Collapsed;
        Width = 48;
    }

    private bool IsCursorNearLeftEdge()
    {
        if (!GetCursorPos(out var point)) return false;
        return point.X >= _monitorBounds.Left &&
               point.X <= _monitorBounds.Left + 26 * _scale &&
               point.Y >= _monitorBounds.Top && point.Y <= _monitorBounds.Bottom;
    }

    private void DarknessSlider_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        // Durante InitializeComponent el Slider puede disparar este evento
        // antes de que el TextBlock haya sido creado por el XAML.
        if (DarknessLabel is not null)
            DarknessLabel.Text = $"{Math.Round(e.NewValue * 100)}%";
        DarknessChanged?.Invoke(e.NewValue);
    }

    private void AddAreaButton_Click(object sender, RoutedEventArgs e)
        => AddAreaRequested?.Invoke();

    private void EditAreaButton_Click(object sender, RoutedEventArgs e)
    {
        if (AreasList.SelectedIndex >= 0)
            EditAreaRequested?.Invoke(AreasList.SelectedIndex);
    }

    private void FollowMouseCheckBox_Changed(object sender, RoutedEventArgs e)
        => FollowMouseChanged?.Invoke(FollowMouseCheckBox.IsChecked == true);

    private void FollowShapeComboBox_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FollowShapeComboBox.SelectedItem is not null)
            FollowShapeChanged?.Invoke(SelectedShape);
    }

    private void FollowSizeSlider_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (FollowSizeLabel is not null)
            FollowSizeLabel.Text = $"{Math.Round(e.NewValue)} px";
        FollowSizeChanged?.Invoke(e.NewValue);
    }

    private void SpecificTextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (SpecificTextCheckBox.IsChecked != true)
            RaiseTextHighlightOptionsChanged();
    }

    private void HighlightNowButton_Click(object sender, RoutedEventArgs e)
    {
        SpecificTextCheckBox.IsChecked = true;
        RaiseTextHighlightOptionsChanged();
    }

    private void RaiseTextHighlightOptionsChanged()
    {
        TextHighlightOptionsChanged?.Invoke(new TextHighlightOptions(
            SpecificTextCheckBox.IsChecked == true,
            SpecificTextBox.Text.Trim()));
    }

    private void RemoveAreaButton_Click(object sender, RoutedEventArgs e)
    {
        if (AreasList.SelectedIndex >= 0)
            RemoveAreaRequested?.Invoke(AreasList.SelectedIndex);
    }

    private void ClearAreasButton_Click(object sender, RoutedEventArgs e)
        => ClearAreasRequested?.Invoke();

    private void PositionWindow()
    {
        Left = _monitorBounds.Left / _scale;
        Top = _topPixels / _scale;
    }

    private static string Describe(FocusArea area)
        => $"{area.Shape switch
        {
            FocusShape.Circle => "Círculo",
            FocusShape.Square => "Cuadrado",
            _ => "Rectángulo"
        }} — {area.Bounds.Width} × {area.Bounds.Height}";

    private sealed record AreaListItem(int Index, string Description)
    {
        public override string ToString() => $"Área {Index + 1}: {Description}";
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
