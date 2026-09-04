using System.Linq;
using System.Xml.Linq;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Shared XAML-parsing helper for the staggered-entrance tests (RemEx-dnfq0, RemEx-alwfa.2).
/// </summary>
/// <remarks>
/// Review of RemEx-dnfq0 asked that a follow-up tie the nth-child style COUNT to the container's
/// actual direct-child count (parsed XAML) rather than a hardcoded number, so a section added to or
/// removed from one of these views fails this test instead of silently animating the wrong number
/// of children (or leaving a new child un-animated).
/// </remarks>
internal static class XamlContainerHelper
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Number of direct child ELEMENTS of the (bare <c>Name</c> or <c>x:Name</c>)'d container,
    /// skipping Avalonia property-element syntax (e.g. <c>ItemsControl.ItemsPanel</c>) — those are
    /// markup for a property value, not a rendered child, and any local name containing '.'
    /// identifies one.
    /// </summary>
    public static int CountDirectChildren(string xamlText, string containerName)
    {
        var doc = XDocument.Parse(xamlText);
        var container = doc.Descendants()
            .Single(e => (string?)e.Attribute("Name") == containerName
                         || (string?)e.Attribute(XNs + "Name") == containerName);

        return container.Elements().Count(e => !e.Name.LocalName.Contains('.'));
    }
}
