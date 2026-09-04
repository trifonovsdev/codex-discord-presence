using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WinRT.Interop;

namespace CodexPresence;

/// <summary>Real pointer and compositor checks, only invoked by --capture-preview.</summary>
internal static class NativeHoverChecks
{
    private sealed record Sample(double Ms, int Phase, double Age, bool Over, int R, int G, int B);
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
        SetForegroundWindow(hwnd);
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin) || !GetCursorPos(out var original))
            throw new InvalidOperationException("Cannot locate the desktop pointer for hover checks.");
        var failures = new List<string>();
        File.AppendAllText(Path.Combine(directory, "input-desktop.txt"),
            $"Session {Process.GetCurrentProcess().SessionId}; active console {WTSGetActiveConsoleSessionId()}; window {hwnd}; foreground {GetForegroundWindow()}; origin {origin.X},{origin.Y}; scale {root.XamlRoot.RasterizationScale}\n");
        try
        {
            foreach (var name in names)
            {
                var button = (ButtonBase)root.FindName(name);
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
                var changedAt = 0.0;
                while (watch.ElapsedMilliseconds < 2800)
                {
                    var next = Array.FindLastIndex(Phases, item => watch.ElapsedMilliseconds >= item.At);
                    if (next != phase)
                    {
                        phase = next;
                        changedAt = watch.Elapsed.TotalMilliseconds;
                        var (_, over, edge) = Phases[phase];
                        var px = over ? x + (edge ? 1 : width / 2) : (int)(root.ActualWidth * scale / 2);
                        var py = over ? y + height / 2 : (int)(130 * scale);
                        MovePointer(origin.X + px, origin.Y + py);
                        GetCursorPos(out var actual);
                        File.AppendAllText(Path.Combine(directory, "input-desktop.txt"),
                            $"{name} phase {phase}: wanted {origin.X + px},{origin.Y + py}; actual {actual.X},{actual.Y}; window at cursor {WindowFromPoint(actual)}\n");
                    }
                    await Task.Delay(16);
                    var frame = DesktopCapture.Capture(window);
                    var pixel = ((y + height / 2) * frame.Width + x + (int)(6 * scale)) * 4;
                    samples.Add(new Sample(watch.Elapsed.TotalMilliseconds, phase, watch.Elapsed.TotalMilliseconds - changedAt, button.IsPointerOver,
                        frame.Pixels[pixel + 2], frame.Pixels[pixel + 1], frame.Pixels[pixel]));
                    frames.Add(Crop(frame, Math.Max(0, x - 4), Math.Max(0, y - 4), width + 8, height + 8));
                }
                await File.WriteAllTextAsync(Path.Combine(folder, "samples.json"),
                    JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                await DesktopCapture.SaveSequenceAsync(folder, frames, samples.Select(sample => sample.Ms / 1000).ToArray());

                // A held hover must stay held, including one physical pixel inside the edge.
                var held = samples.Where(sample => sample.Age >= 180).ToArray();
                Check(held.Length >= 8 && held.All(sample => sample.Over == Phases[sample.Phase].Over),
                    $"{name}: real pointer stays stable, including the edge", directory, failures);
                var normal = held.Where(sample => !Phases[sample.Phase].Over).ToArray();
                var hover = held.Where(sample => Phases[sample.Phase].Over).ToArray();
                var settled = normal.Concat(hover).ToArray();
                var expected = name == "SaveButton" ? (R: 255, G: 255, B: 255)
                    : name == "PrivacyNavButton" ? (R: 28, G: 29, B: 32) : (R: 34, G: 36, B: 40);
                Check(hover.Length > 0 && hover.All(sample => Math.Abs(sample.R - expected.R) <= 2 &&
                        Math.Abs(sample.G - expected.G) <= 2 && Math.Abs(sample.B - expected.B) <= 2),
                    $"{name}: hovered pixels use the graphite palette, not the Windows accent", directory, failures);
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
    }

    public static void ThrowIfFailed(string directory)
    {
        var failures = File.ReadAllLines(Path.Combine(directory, "interaction-checks.txt"))
            .Where(line => line.StartsWith("FAIL", StringComparison.Ordinal)).ToArray();
        if (failures.Length > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }

    public static async Task CheckPressAsync(MainWindow window, string directory)
    {
        if (GetForegroundWindow() != WindowNative.GetWindowHandle(window))
            throw new InvalidOperationException("Preview lost foreground focus; leave the desktop idle during capture.");
        var root = (FrameworkElement)window.Content;
        var button = (Button)root.FindName("PauseButton");
        var position = button.TransformToVisual(root).TransformPoint(new Point());
        var origin = new NativePoint();
        if (!ClientToScreen(WindowNative.GetWindowHandle(window), ref origin) || !GetCursorPos(out var original))
            throw new InvalidOperationException("Cannot locate the button for native input checks.");
        var scale = root.XamlRoot.RasterizationScale;
        var centerX = origin.X + (int)((position.X + button.ActualWidth / 2) * scale);
        var centerY = origin.Y + (int)((position.Y + button.ActualHeight / 2) * scale);
        var clicks = 0;
        var failures = new List<string>();
        var frames = new List<DesktopCapture.Frame>();
        var seconds = new List<double>();
        var clock = Stopwatch.StartNew();
        void Clicked(object sender, RoutedEventArgs args) => clicks++;
        button.Click += Clicked;
        try
        {
            Move(false);
            await RecordAsync(220);
            Move(true);
            await RecordAsync(320);
            Mouse(2);
            await RecordAsync(110);
            Check(button.IsPressed, "Pointer down reaches the native pressed state", directory, failures);
            Mouse(4);
            await RecordAsync(260);
            Check(clicks == 1 && !button.IsPressed, "A pointer click fires once and settles", directory, failures);
            Mouse(2);
            await RecordAsync(70);
            Move(false);
            await RecordAsync(180);
            Mouse(4);
            await RecordAsync(220);
            Check(clicks == 1 && !button.IsPressed, "Dragging out cancels the click and releases feedback", directory, failures);
            Move(true);
            await RecordAsync(220);
            Mouse(2);
            await RecordAsync(110);
            Mouse(4);
            await RecordAsync(260);
            Move(false);
            await RecordAsync(220);
            Check(clicks == 2 && !button.IsPressed, "Press feedback recovers after a cancelled click", directory, failures);

            button.Focus(FocusState.Keyboard);
            Key(0x20, false);
            await Task.Delay(45);
            Key(0x20, true);
            await Task.Delay(160);
            Key(0x0D, false);
            Key(0x0D, true);
            await Task.Delay(160);
            Check(clicks == 4 && !button.IsPressed, "Space and Enter each activate once without a stuck press", directory, failures);
            Move(true);
            Mouse(2);
            await Task.Delay(45);
            button.IsEnabled = false;
            Mouse(4);
            await Task.Delay(180);
            Check(clicks == 4 && !button.IsPressed, "Disabling a pressed button cancels its click", directory, failures);
            button.IsEnabled = true;
            Move(false);
            await Task.Delay(200);
            var content = FindDescendant(root.FindName("PauseButton") as DependencyObject, "InteractionContent");
            if (content is not null)
                Check(ElementCompositionPreview.GetElementVisual(content).Scale == System.Numerics.Vector3.One,
                    "Content returns to its resting scale after cancellation and keyboard input", directory, failures);
        }
        finally
        {
            Mouse(4);
            Key(0x20, true);
            Key(0x0D, true);
            button.IsEnabled = true;
            button.Click -= Clicked;
            SetCursorPos(original.X, original.Y);
        }
        await DesktopCapture.SaveSequenceAsync(Path.Combine(directory, "button-motion"), frames, seconds);

        void Move(bool over)
        {
            MovePointer(over ? centerX : origin.X + 100, over ? centerY : origin.Y + 130);
        }
        async Task RecordAsync(int duration)
        {
            var end = clock.ElapsedMilliseconds + duration;
            while (clock.ElapsedMilliseconds < end)
            {
                await Task.Delay(16);
                seconds.Add(clock.Elapsed.TotalSeconds);
                frames.Add(DesktopCapture.Capture(window, includeCursor: true));
            }
        }
    }

    private static UIElement? FindDescendant(DependencyObject? root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        if (root is null) return null;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendant(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
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
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    private static void Mouse(uint flags) => Send(new Input { Data = new InputData { Mouse = new MouseInput { Flags = flags } } });
    private static void Key(ushort key, bool up) => Send(new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } });

    private static void MovePointer(int x, int y) => Send(new Input
    {
        Data = new InputData
        {
            Mouse = new MouseInput
            {
                X = (int)((((long)x - GetSystemMetrics(76)) * 65536 + 32768) / GetSystemMetrics(78)),
                Y = (int)((((long)y - GetSystemMetrics(77)) * 65536 + 32768) / GetSystemMetrics(79)),
                Flags = 0xE001, // MOVE | MOVE_NOCOALESCE | ABSOLUTE | VIRTUALDESK
            },
        },
    });

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"Cannot inject preview pointer input ({Marshal.GetLastWin32Error()}).");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
}
