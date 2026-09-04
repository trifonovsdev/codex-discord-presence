using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace CodexPresence;

/// <summary>Native input states drive stable, independent visual layers.</summary>
public sealed class InteractionStateManager : VisualStateManager
{
    private string? previousState;

    protected override bool GoToStateCore(Control control, FrameworkElement templateRoot, string stateName,
        VisualStateGroup group, VisualState state, bool useTransitions)
    {
        var changed = base.GoToStateCore(control, templateRoot, stateName, group, state, useTransitions);
        if (!changed || group.Name != "CommonStates" || previousState == stateName) return changed;

        var disabled = stateName == "Disabled";
        var keyboard = control.FocusState == FocusState.Keyboard;
        var animate = useTransitions && templateRoot.IsLoaded && previousState is not null and not "Disabled"
            && !disabled && !new AccessibilitySettings().HighContrast;
        var duration = stateName == "Pressed" ? 90 : 140;
        SetLayer("HoverSurface", stateName is "PointerOver" or "Pressed", animate && !keyboard);
        SetLayer("PressedSurface", stateName == "Pressed", animate && !keyboard);
        SetLayer("DisabledSurface", disabled, false);
        if ((stateName == "Pressed" || previousState == "Pressed" || disabled) &&
            templateRoot.FindName("InteractionContent") is FrameworkElement content)
            Motion.Press(content, stateName == "Pressed", animate && !keyboard);
        previousState = stateName;
        return changed;

        void SetLayer(string name, bool visible, bool transition)
        {
            if (templateRoot.FindName(name) is UIElement layer)
                Motion.Fade(layer, visible ? 1f : 0f, duration, transition);
        }
    }
}
