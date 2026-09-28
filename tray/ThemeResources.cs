using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CodexPresence;

/// <summary>
/// Appearance preference and theme-aware lookups for brushes assigned from code.
/// Windows switch themes per window, so a lookup must follow the element's
/// theme rather than the application-wide one.
/// </summary>
internal static class ThemeResources
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static ElementTheme ElementThemeFor(string? appearance) => appearance?.Trim().ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Application theme for the configured appearance, or null to follow Windows.</summary>
    public static ApplicationTheme? ApplicationThemeFor(string? appearance) => appearance?.Trim().ToLowerInvariant() switch
    {
        "light" => ApplicationTheme.Light,
        "dark" => ApplicationTheme.Dark,
        _ => null,
    };

    public static void Apply(FrameworkElement root, string? appearance) =>
        root.RequestedTheme = ElementThemeFor(appearance);

    public static Brush Brush(FrameworkElement scope, string key)
    {
        var dictionaries = Application.Current.Resources.ThemeDictionaries;
        var theme = Accessibility.HighContrast
            ? "HighContrast"
            : scope.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        if (dictionaries.TryGetValue(theme, out var value) &&
            value is ResourceDictionary dictionary &&
            dictionary.TryGetValue(key, out var resource) &&
            resource is Brush brush)
        {
            return brush;
        }

        return (Brush)Application.Current.Resources[key];
    }

    public static Windows.UI.Color Color(FrameworkElement scope, string key) =>
        Brush(scope, key) is SolidColorBrush solid ? solid.Color : Microsoft.UI.Colors.Transparent;
}
