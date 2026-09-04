using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinRT.Interop;

namespace CodexPresence;

/// <summary>Real pointer and compositor checks, only invoked by --capture-preview.</summary>
internal static class NativeHoverChecks
{
    private sealed record Sample(double Ms, int Phase, bool Over, int R, int G, int B);
    private static readonly (int At, bool Over, bool Edge)[] Phases =
    [
        (0, false, false), (240, true, false), (800, false, false),
        (1050, true, false), (1110, false, false), (1160, true, false),
        (1210, false, false), (1260, true, false), (1310, false, false),
        (1360, true, false), (1760, false, false), (2080, true, true), (2480, false, false),
    ];

    public static async Task RunAsync(Window window, string directory, params string[] names)
    {
        var root = (FrameworkElement)window.Content;
        var hwnd = WindowNative.GetWindowHandle(window);
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin) || !GetCursorPos(out var original))
            throw new InvalidOperationException("Cannot locate the desktop pointer for hover checks.");
        var failures = new List<string>();
        try
        {
            foreach (var name in names)
            {
                var button = (Control)root.FindName(name);
                var position = button.TransformToVisual(root).TransformPoint(new Point());
                var scale = root.XamlRoot.RasterizationScale;
                var x = (int)Math.Round(position.X * scale);
                var y = (int)Math.Round(position.Y * scale);
                var width = (int)Math.Round(button.ActualWidth * scale);
                var height = (int)Math.Round(button.ActualHeight * scale);
                var frames = new List<DesktopCapture.Frame>();
                var samples = new List<Sample>();
                var folder = Path.Combine(directory, "hover", name);
                Directory.CreateDirectory(folder);
                var watch = Stopwatch.StartNew();
                var phase = -1;
                while (watch.ElapsedMilliseconds < 2800)
                {
                    var next = Array.FindLastIndex(Phases, item => watch.ElapsedMilliseconds >= item.At);
                    if (next != phase)
                    {
                        phase = next;
                        var (at, over, edge) = Phases[phase];
                        var px = over ? x + (edge ? 1 : width / 2) : (int)(root.ActualWidth * scale / 2);
                        var py = over ? y + height / 2 : (int)(24 * scale);
                        if (!SetCursorPos(origin.X + px, origin.Y + py))
                            throw new InvalidOperationException("Cannot move the desktop pointer for hover checks.");
                    }
                    await Task.Delay(16);
                    var frame = DesktopCapture.Capture(window);
                    var pixel = ((y + height / 2) * frame.Width + x + (int)(6 * scale)) * 4;
                    samples.Add(new Sample(watch.Elapsed.TotalMilliseconds, phase, button.IsPointerOver,
                        frame.Pixels[pixel + 2], frame.Pixels[pixel + 1], frame.Pixels[pixel]));
                    frames.Add(Crop(frame, Math.Max(0, x - 4), Math.Max(0, y - 4), width + 8, height + 8));
                }
                await File.WriteAllTextAsync(Path.Combine(folder, "samples.json"),
                    JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                var concat = new List<string>();
                for (var i = 0; i < frames.Count; i++)
                {
                    await DesktopCapture.SaveAsync(frames[i], Path.Combine(folder, $"frame-{i:D4}.png"));
                    concat.Add($"file 'frame-{i:D4}.png'");
                    var duration = i + 1 < samples.Count ? (samples[i + 1].Ms - samples[i].Ms) / 1000 : 0.25;
                    concat.Add("duration " + duration.ToString("F6", CultureInfo.InvariantCulture));
                }
                concat.Add($"file 'frame-{frames.Count - 1:D4}.png'");
                File.WriteAllLines(Path.Combine(folder, "frames.txt"), concat);

                // A held hover must stay held, including one physical pixel inside the edge.
                var held = samples.Where(sample => sample.Ms - Phases[sample.Phase].At >= 180).ToArray();
                Check(held.Length >= 8 && held.All(sample => sample.Over == Phases[sample.Phase].Over),
                    $"{name}: real pointer stays stable, including the edge", directory, failures);
                var normal = held.Where(sample => !Phases[sample.Phase].Over).ToArray();
                var hover = held.Where(sample => Phases[sample.Phase].Over).ToArray();
                var settled = normal.Concat(hover).ToArray();
                Check(normal.Length > 0 && hover.Length > 0 && samples.All(sample =>
                        sample.R >= settled.Min(item => item.R) - 2 && sample.R <= settled.Max(item => item.R) + 2 &&
                        sample.G >= settled.Min(item => item.G) - 2 && sample.G <= settled.Max(item => item.G) + 2 &&
                        sample.B >= settled.Min(item => item.B) - 2 && sample.B <= settled.Max(item => item.B) + 2),
                    $"{name}: no bright/dark flash outside the endpoint colors during reversals", directory, failures);
                Check(normal.Select(sample => (sample.R, sample.G, sample.B)).Distinct().Count() <= 2 &&
                        hover.Select(sample => (sample.R, sample.G, sample.B)).Distinct().Count() <= 2,
                    $"{name}: held states do not blink", directory, failures);
            }
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }

    private static void Check(bool passed, string message, string directory, List<string> failures)
    {
        File.AppendAllText(Path.Combine(directory, "interaction-checks.txt"), $"{(passed ? "PASS" : "FAIL")} {message}\n");
        if (!passed) failures.Add(message);
    }

    private static DesktopCapture.Frame Crop(DesktopCapture.Frame source, int x, int y, int width, int height)
    {
        width = Math.Min(width, source.Width - x);
        height = Math.Min(height, source.Height - y);
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Array.Copy(source.Pixels, ((y + row) * source.Width + x) * 4, pixels, row * width * 4, width * 4);
        return new DesktopCapture.Frame(width, height, pixels);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
