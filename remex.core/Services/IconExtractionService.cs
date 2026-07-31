namespace Remex.Core.Services;

/// <summary>
/// Service to extract icons from files/executables.
/// </summary>
public interface IIconExtractionService
{
    string ExtractIconAsBase64(string filePath);
}

public class IconExtractionService : IIconExtractionService
{
    // A simple 16x16 or 32x32 blank SVG/PNG or geometric shape embedded as base64 string
    public const string FallbackBase64Icon = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAACXBIWXMAAAsTAAALEwEAmpwYAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAACNSURBVHgB7dexCQBACAAw3L/nCgY2YWcRweD0kUDeP/P1vT8AAAAASUVORK5CYII=";

    public string ExtractIconAsBase64(string filePath)
    {
        return FallbackBase64Icon;
    }
}
