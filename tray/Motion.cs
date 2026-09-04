using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace CodexPresence;

internal static class Motion
{
    private static readonly Lazy<UISettings?> SystemSettings = new(CreateSystemSettings);

    public static void Fade(UIElement element, float opacity, int milliseconds = 140, bool animate = true)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!animate || !AnimationsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = opacity;
            return;
        }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        animation.InsertKeyFrame(1f, opacity, easing);
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        // No explicit start frame: rapid reversals continue from the current visual value.
        visual.StartAnimation("Opacity", animation);
    }

    public static void Press(FrameworkElement content, bool pressed, bool animate)
    {
        // Only the label responds to a press. The surface and pointer target stay fixed.
        var visual = ElementCompositionPreview.GetElementVisual(content);
        visual.CenterPoint = new Vector3((float)content.ActualWidth / 2, (float)content.ActualHeight / 2, 0);
        var target = pressed && animate ? new Vector3(0.97f, 0.97f, 1f) : Vector3.One;
        if (!animate || !AnimationsEnabled)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            return;
        }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        animation.InsertKeyFrame(1f, target, easing);
        animation.Duration = TimeSpan.FromMilliseconds(pressed ? 90 : 160);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        visual.StartAnimation("Scale", animation);
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
