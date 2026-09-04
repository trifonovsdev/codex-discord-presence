using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexPresence;

internal static class NativeInteractionChecks
{
    public static async Task RunAsync(SettingsWindow window, string directory)
    {
        var root = (FrameworkElement)window.Content;
        T Find<T>(string name) where T : FrameworkElement => (T)root.FindName(name);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            File.AppendAllText(Path.Combine(directory, "interaction-checks.txt"), $"PASS {message}\n");
        }

        foreach (var section in new[] { "privacy", "remote", "general", "privacy", "general" })
        {
            window.ShowPage(section);
            await Task.Delay(40);
            Check(new[] { "GeneralNavButton", "PrivacyNavButton", "RemoteNavButton" }
                .Count(name => Find<RadioButton>(name).IsChecked == true) == 1, $"Exactly one tab selected after {section}");
        }
        var toggle = Find<ToggleSwitch>("PresenceToggle");
        var button = Find<Button>("SaveButton");
        var toggleWidth = toggle.ActualWidth;
        var buttonWidth = button.ActualWidth;
        foreach (var state in new[] { "PointerOver", "Pressed", "Normal", "PointerOver", "Normal" })
        {
            VisualStateManager.GoToState(button, state, true);
            await Task.Delay(45);
            Check(Math.Abs(button.ActualWidth - buttonWidth) < 0.01, $"Button bounds stay fixed in {state}");
        }

        // This is captured from the real Windows compositor, not a slideshow or simulated tween.
        var frames = Path.Combine(directory, "motion");
        Directory.CreateDirectory(frames);
        var timestamps = new List<double>();
        var capturedFrames = new List<DesktopCapture.Frame>();
        var watch = Stopwatch.StartNew();
        var original = toggle.IsOn;
        while (watch.ElapsedMilliseconds < 2600)
        {
            var ms = watch.ElapsedMilliseconds;
            toggle.IsOn = ms is >= 300 and < 750 or >= 850 and < 1600 or >= 2000;
            timestamps.Add(watch.Elapsed.TotalSeconds);
            capturedFrames.Add(DesktopCapture.Capture(window));
            await Task.Delay(16);
        }
        toggle.IsOn = original;
        await Task.Delay(180);
        Check(Math.Abs(toggle.ActualWidth - toggleWidth) < 0.01 && toggle.IsOn == original,
            "Rapid toggle reversals settle without changing hitbox or saved configuration");
        for (var frame = 0; frame < capturedFrames.Count; frame++)
            await DesktopCapture.SaveAsync(capturedFrames[frame], Path.Combine(frames, $"frame-{frame:D4}.png"));
        capturedFrames.Clear();
        var concat = new List<string>();
        for (var frame = 0; frame < timestamps.Count; frame++)
        {
            concat.Add($"file 'frame-{frame:D4}.png'");
            var duration = frame + 1 < timestamps.Count ? timestamps[frame + 1] - timestamps[frame] : 0.4;
            concat.Add("duration " + duration.ToString("F6", CultureInfo.InvariantCulture));
        }
        concat.Add($"file 'frame-{timestamps.Count - 1:D4}.png'");
        File.WriteAllLines(Path.Combine(frames, "frames.txt"), concat);
        File.AppendAllText(Path.Combine(directory, "interaction-checks.txt"),
            $"Native frames: {timestamps.Count}; Windows animations enabled: {new Windows.UI.ViewManagement.UISettings().AnimationsEnabled}\n");

        WindowSizing.ResizeInDips(window, 700, 480);
        await Task.Delay(160);
        foreach (var name in new[] { "PresenceToggle", "ActivityNameInput", "LanguageSelect", "SaveButton" })
        {
            var element = Find<FrameworkElement>(name);
            var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
            Check(point.X >= 0 && point.X + element.ActualWidth <= root.ActualWidth,
                $"{name} fits at the minimum window width");
        }
        WindowSizing.ResizeInDips(window, 740, 620);
    }
}
