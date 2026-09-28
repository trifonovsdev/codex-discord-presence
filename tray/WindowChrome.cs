using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace CodexPresence;

internal static class WindowChrome
{
    public static void Apply(Window window)
    {
        var titleBar = window.AppWindow.TitleBar;
        var scope = window.Content as FrameworkElement;
        Windows.UI.Color Color(string key) => scope is null
            ? ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources[key]).Color
            : ThemeResources.Color(scope, key);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonInactiveForegroundColor = Color("TextMutedBrush");
        titleBar.ButtonHoverForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonHoverBackgroundColor = Color("SurfaceHoverBrush");
        titleBar.ButtonPressedForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonPressedBackgroundColor = Color("SurfacePressedBrush");
    }
}
