namespace Enfoque;

/// <summary>Configuración de una búsqueda manual de texto.</summary>
public sealed record TextHighlightOptions(
    bool SpecificTextEnabled,
    string SpecificText);
