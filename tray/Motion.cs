using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace CodexPresence;

internal static class Motion
{
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(140);
    private static readonly Lazy<UISettings?> SystemSettings = new(CreateSystemSettings);

    public static void AttachButtonFeedback(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            button.Loaded += (_, _) => CenterVisual(button);
            button.SizeChanged += (_, _) => CenterVisual(button);
            button.PointerEntered += (_, _) => AnimateScale(button, 1.008f, HoverDuration);
            button.PointerPressed += (_, _) => AnimateScale(button, 0.985f, PressDuration);
            button.PointerReleased += (_, _) => AnimateScale(button, 1.008f, PressDuration);
            button.PointerExited += (_, _) => AnimateScale(button, 1f, HoverDuration);
            button.PointerCanceled += (_, _) => AnimateScale(button, 1f, PressDuration);
            button.PointerCaptureLost += (_, _) => AnimateScale(button, 1f, PressDuration);
        }
    }

    private static void CenterVisual(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)element.ActualWidth / 2f,
            (float)element.ActualHeight / 2f,
            0f);
    }

    private static void AnimateScale(Button button, float value, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        CenterVisual(button);

        if (!AnimationsEnabled)
        {
            visual.Scale = Vector3.One;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(value, value, 1f), easing);
        animation.Duration = duration;
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
