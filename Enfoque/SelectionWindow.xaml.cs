using System.Windows;
using System.Windows.Media;
using Input = System.Windows.Input;

namespace Enfoque;

/// <summary>
/// Capa temporal para dibujar una o varias áreas transparentes sobre el
/// monitor seleccionado.
/// </summary>
public partial class SelectionWindow : Window
{
    private readonly System.Drawing.Rectangle _monitorBounds;
    private readonly FocusShape _shape;
    private readonly double _scale;
    private readonly List<FocusArea> _areas = [];
    private System.Windows.Point _startPoint;
    private bool _isDrawing;
    private bool _completed;

    public event Action<IReadOnlyList<FocusArea>?>? SelectionCompleted;

    public SelectionWindow(System.Drawing.Rectangle monitorBounds, FocusShape shape, double scale,
        IReadOnlyList<FocusArea>? existingAreas = null)
    {
        InitializeComponent();
        _monitorBounds = monitorBounds;
        _shape = shape;
        _scale = scale;
        if (existingAreas is not null)
            _areas.AddRange(existingAreas);

        Left = monitorBounds.Left / scale;
        Top = monitorBounds.Top / scale;
        Width = monitorBounds.Width / scale;
        Height = monitorBounds.Height / scale;

        PreviewKeyDown += SelectionWindow_PreviewKeyDown;
        KeyDown += SelectionWindow_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            Focus();
            SelectionCanvas.Focus();
            UpdateSelectionMask(null);
        };

        if (_areas.Count > 0)
            InstructionText.Text = $"Areas actuales: {_areas.Count}. Dibuja otra o presiona ENTER para terminar | ESC cancelar";
    }

    private void SelectionCanvas_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(SelectionCanvas);
        _isDrawing = true;
        SelectionCanvas.CaptureMouse();
        SelectionOutline.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
        e.Handled = true;
    }

    private void SelectionCanvas_MouseMove(object sender, Input.MouseEventArgs e)
    {
        if (_isDrawing)
            UpdateSelection(e.GetPosition(SelectionCanvas));
    }

    private void SelectionCanvas_MouseLeftButtonUp(object sender, Input.MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;

        var bounds = GetSelectionBounds(e.GetPosition(SelectionCanvas));
        _isDrawing = false;
        SelectionCanvas.ReleaseMouseCapture();
        e.Handled = true;

        if (bounds.Width >= 8 && bounds.Height >= 8)
        {
            _areas.Add(new FocusArea(ToScreenRect(bounds), _shape));
            InstructionText.Text = $"Areas dibujadas: {_areas.Count}. Dibuja otra o presiona ENTER para terminar | ESC cancelar";
        }

        SelectionOutline.Visibility = Visibility.Collapsed;
        UpdateSelectionMask(null);
    }

    private void UpdateSelection(System.Windows.Point current)
    {
        var bounds = GetSelectionBounds(current);
        var localGeometry = CreateLocalGeometry(bounds, _shape);
        SelectionOutline.Data = localGeometry;
        UpdateSelectionMask(bounds);
    }

    private void UpdateSelectionMask(Rect? preview)
    {
        var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));
        var clearAreas = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (var area in _areas)
        {
            var bounds = new Rect(
                (area.Bounds.X - _monitorBounds.Left) / _scale,
                (area.Bounds.Y - _monitorBounds.Top) / _scale,
                area.Bounds.Width / _scale,
                area.Bounds.Height / _scale);
            clearAreas.Children.Add(CreateLocalGeometry(bounds, area.Shape));
        }

        if (preview is Rect current && current.Width > 0 && current.Height > 0)
            clearAreas.Children.Add(CreateLocalGeometry(current, _shape));

        SelectionMask.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude, outer, clearAreas);
    }

    private Geometry CreateLocalGeometry(Rect bounds, FocusShape shape)
        => shape == FocusShape.Circle
            ? new EllipseGeometry(bounds)
            : new RectangleGeometry(bounds);

    private Rect GetSelectionBounds(System.Windows.Point current)
    {
        var left = Math.Min(_startPoint.X, current.X);
        var top = Math.Min(_startPoint.Y, current.Y);
        var width = Math.Abs(current.X - _startPoint.X);
        var height = Math.Abs(current.Y - _startPoint.Y);

        if (_shape == FocusShape.Square || _shape == FocusShape.Circle)
        {
            var side = Math.Min(width, height);
            width = side;
            height = side;
            if (current.X < _startPoint.X) left = _startPoint.X - side;
            if (current.Y < _startPoint.Y) top = _startPoint.Y - side;
        }

        return new Rect(left, top, width, height);
    }

    private System.Windows.Int32Rect ToScreenRect(Rect bounds)
    {
        return new System.Windows.Int32Rect(
            _monitorBounds.Left + (int)Math.Round(bounds.X * _scale),
            _monitorBounds.Top + (int)Math.Round(bounds.Y * _scale),
            (int)Math.Round(bounds.Width * _scale),
            (int)Math.Round(bounds.Height * _scale));
    }

    private void SelectionWindow_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Enter)
        {
            Complete(_areas.Count > 0 ? _areas.ToArray() : null);
            e.Handled = true;
        }
        else if (e.Key == Input.Key.Escape)
        {
            Complete(null);
            e.Handled = true;
        }
    }

    private void Complete(IReadOnlyList<FocusArea>? areas)
    {
        if (_completed) return;
        _completed = true;
        SelectionCompleted?.Invoke(areas);
    }
}
