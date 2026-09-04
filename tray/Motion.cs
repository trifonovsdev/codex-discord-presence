using System.Numerics;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace CodexPresence;

internal static class Motion
{
    private static readonly Lazy<UISettings?> SystemSettings = new(CreateSystemSettings);

    public static void Fade(Microsoft.UI.Xaml.UIElement element, float opacity, int milliseconds = 140)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!AnimationsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = opacity;
            return;
        }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        animation.InsertKeyFrame(1f, opacity, easing);
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        // No explicit start frame: rapid reversals continue from the current visual value.
        visual.StartAnimation("Opacity", animation);
    }

    public static void Reveal(Microsoft.UI.Xaml.UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = AnimationsEnabled ? 0.6f : 1f;
        Fade(element, 1f);
    }

    private static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return SystemSettings.Value?.AnimationsEnabled == true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static UISettings? CreateSystemSettings()
    {
        try
        {
            return new UISettings();
        }
        catch
        {
            return null;
        }
    }
}
