namespace Enfoque;

/// <summary>Formas disponibles para un área de enfoque.</summary>
public enum FocusShape
{
    Rectangle,
    Square,
    Circle
}

/// <summary>Área guardada usando coordenadas absolutas del monitor.</summary>
public sealed record FocusArea(System.Windows.Int32Rect Bounds, FocusShape Shape);
