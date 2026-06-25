using System.Windows.Input;

namespace Remex.Desktop.Models;

/// <summary>
/// A single entry in the command palette — a searchable action with a label, category, and command.
/// </summary>
public sealed record CommandPaletteEntry(string Label, string Category, ICommand Command);
