using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Remex.Branding;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// Live Handshake's words from the app's .resx (RemEx-8g6n0): the console lines and the node suffix,
/// in sentence case, formatted with the app's current culture. Falls back to English for any key that
/// does not resolve.
/// </summary>
/// <remarks>
/// A failed lookup or a malformed translation degrades to the English line rather than throwing into
/// the splash, and is logged ONCE per key and exception type as a warning with its exception — the
/// console is re-read many times a second, so logging every occurrence would bury the log.
/// </remarks>
public sealed class LocalizedHandshakeText : ILiveHandshakeText
{
    private readonly Func<ILogger?> _logger;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    public LocalizedHandshakeText()
        : this(() => App.Services?.GetService(typeof(ILogger<LocalizedHandshakeText>)) as ILogger)
    {
    }

    /// <summary>Test seam: supply the logger (resolved lazily, at the first failure).</summary>
    public LocalizedHandshakeText(Func<ILogger?> logger) => _logger = logger;

    public string LinkedSuffix => Lookup("Splash_LiveHandshake_NodeLinked")
        ?? EnglishHandshakeText.Instance.LinkedSuffix;

    public string Line(HandshakeLine line)
    {
        string? text = line.Kind switch
        {
            HandshakeLineKind.Paired => Plural("Splash_LiveHandshake_Paired", line.Count),
            HandshakeLineKind.NonePaired => Lookup("Splash_LiveHandshake_NonePaired"),
            HandshakeLineKind.Listening => line.Port is { } port
                ? Format("Splash_LiveHandshake_ListeningOnPort", port)
                : Lookup("Splash_LiveHandshake_Listening"),
            HandshakeLineKind.Linked => Format("Splash_LiveHandshake_IsLinked", line.Name ?? string.Empty),
            HandshakeLineKind.Opening => Lookup("Splash_LiveHandshake_Opening"),
            _ => null,
        };
        return text ?? EnglishHandshakeText.Instance.Line(line);
    }

    private string? Lookup(string key)
    {
        try
        {
            var value = LocalizationService.Instance[key];
            // LocalizationService returns the key itself when it has no such resource.
            return string.IsNullOrEmpty(value) || value == key ? null : value;
        }
        catch (Exception ex)
        {
            LogOnce(key, ex);
            return null;
        }
    }

    private string? Format(string key, params object[] args)
    {
        var template = Lookup(key);
        if (template is null) return null;
        try { return string.Format(Culture, template, args); }
        catch (FormatException ex)
        {
            LogOnce(key, ex);
            return null;
        }
    }

    private string? Plural(string baseKey, int count)
    {
        string category;
        try { category = PluralRules.Category(LocalizationService.Instance.CultureTag, count).ToString(); }
        catch (Exception ex)
        {
            LogOnce(baseKey + " (plural category)", ex);
            category = nameof(PluralCategory.Other);
        }
        var key = $"{baseKey}_{category}";
        var template = Lookup(key) ?? Lookup($"{baseKey}_{nameof(PluralCategory.Other)}");
        if (template is null) return null;
        try { return string.Format(Culture, template, count); }
        catch (FormatException ex)
        {
            LogOnce(key, ex);
            return null;
        }
    }

    private CultureInfo Culture
    {
        get
        {
            try { return LocalizationService.Instance.Culture; }
            catch (Exception ex)
            {
                LogOnce("culture", ex);
                return CultureInfo.CurrentUICulture;
            }
        }
    }


    private void LogOnce(string what, Exception ex)
    {
        // Locked: the status line is read on the render thread, the suffix on the UI thread.
        lock (_logged)
        {
            if (!_logged.Add(what + "|" + ex.GetType().FullName)) return;
        }
        try
        {
            _logger()?.LogWarning(ex,
                "Live Handshake splash: localized text for {What} failed; showing the English line instead", what);
        }
        catch (Exception)
        {
            // Logging must never be the thing that breaks the splash; there is nowhere left to report to.
        }
    }
}
