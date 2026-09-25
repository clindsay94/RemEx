namespace Remex.Core.Models;

/// <summary>
/// Represents a customizable launcher entry for an application.
/// </summary>
public record AppEntry(
    Guid Id,
    string DisplayName,
    string TargetPath,
    string HexColor,
    string? IconBase64,
    int Order = 0,
    // Perf audit P2-18: set once a re-extraction attempt still produced an icon too small for the
    // launcher tile (the target genuinely has nothing sharper - GDI+/shell icon extraction is a
    // property of the target file, not something a retry ever fixes). Skips the same futile
    // GDI+/shell work on every future launch instead of retrying forever. Additive, no
    // protocolVersion bump (RG:396-401) - PC-only field: Android's own AppEntry model only ever
    // reads name/path/icon and never sends entries back, so it never even parses this field.
    bool IconUpgradeUnfixable = false
);
