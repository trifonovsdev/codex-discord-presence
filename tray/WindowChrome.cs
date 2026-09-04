using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexPresence;

internal static class WindowChrome
{
    public static void Apply(Window window)
    {
        var titleBar = window.AppWindow.TitleBar;
        Windows.UI.Color Color(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonInactiveForegroundColor = Color("TextMutedBrush");
        titleBar.ButtonHoverForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonHoverBackgroundColor = Color("SurfaceHoverBrush");
        titleBar.ButtonPressedForegroundColor = Color("TextPrimaryBrush");
        titleBar.ButtonPressedBackgroundColor = Color("SurfaceRaisedBrush");
    }
}
