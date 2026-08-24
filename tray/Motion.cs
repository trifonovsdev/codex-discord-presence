using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace CodexPresence;

internal static class Motion
{
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(120);
    private static readonly Lazy<UISettings?> SystemSettings = new(CreateSystemSettings);

    public static void AttachButtonFeedback(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            button.PointerPressed += (_, _) => AnimateOpacity(button, 0.88f, PressDuration);
            button.PointerReleased += (_, _) => AnimateOpacity(button, 1f, SettleDuration);
            button.PointerCanceled += (_, _) => AnimateOpacity(button, 1f, SettleDuration);
            button.PointerCaptureLost += (_, _) => AnimateOpacity(button, 1f, SettleDuration);
        }
    }

    private static void AnimateOpacity(Button button, float value, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);

        if (!AnimationsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = value;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, value, easing);
        animation.Duration = duration;
        visual.StartAnimation("Opacity", animation);
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
