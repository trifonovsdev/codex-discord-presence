using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexPresence;

public enum MotionEasing
{
    Linear,
    EaseOutCubic,
    EaseInOutCubic,
    SpringOut,
}

/// <summary>
/// A single frame clock shared by every animated control. WinForms timers are
/// message-loop timers, so one clock avoids dozens of independently drifting
/// callbacks while keeping every transition on the UI thread.
/// </summary>
public static class MotionClock
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private static readonly System.Windows.Forms.Timer Clock = new() { Interval = 16 };
    private static readonly List<MotionEntry> Entries = [];

    static MotionClock() => Clock.Tick += (_, _) => Tick();

    public static bool IsReduced => SystemInformation.HighContrast || !ClientAreaAnimationEnabled();

    public static IDisposable Animate(
        Control owner,
        int durationMs,
        Action<float> frame,
        MotionEasing easing = MotionEasing.EaseOutCubic,
        int delayMs = 0,
        Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(frame);

        if (durationMs <= 0 || IsReduced || !owner.IsHandleCreated)
        {
            frame(1f);
            completed?.Invoke();
            return EmptyHandle.Instance;
        }

        var entry = new MotionEntry(owner, durationMs, delayMs, easing, frame, completed, loop: false);
        Entries.Add(entry);
        Clock.Start();
        return entry;
    }

    public static IDisposable Loop(Control owner, int durationMs, Action<float> frame)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(frame);

        if (durationMs <= 0 || IsReduced || !owner.IsHandleCreated)
        {
            frame(0f);
            return EmptyHandle.Instance;
        }

        var entry = new MotionEntry(owner, durationMs, 0, MotionEasing.Linear, frame, null, loop: true);
        Entries.Add(entry);
        Clock.Start();
        return entry;
    }

    public static float Ease(float value, MotionEasing easing)
    {
        var t = Math.Clamp(value, 0f, 1f);
        return easing switch
        {
            MotionEasing.Linear => t,
            MotionEasing.EaseInOutCubic => t < .5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
            MotionEasing.SpringOut when t < 1f => 1f - MathF.Exp(-7.5f * t) * MathF.Cos(10.5f * t),
            MotionEasing.SpringOut => 1f,
            _ => 1f - MathF.Pow(1f - t, 3f),
        };
    }

    public static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        for (var index = Entries.Count - 1; index >= 0; index--)
        {
            var entry = Entries[index];
            if (entry.Cancelled || !entry.Owner.TryGetTarget(out var owner) || owner.IsDisposed)
            {
                Entries.RemoveAt(index);
                continue;
            }

            if (!owner.Visible)
            {
                Entries.RemoveAt(index);
                if (!entry.Loop) entry.Frame(1f);
                continue;
            }
            var elapsedMs = (now - entry.StartTimestamp) * 1000d / Stopwatch.Frequency - entry.DelayMs;
            if (elapsedMs < 0) continue;

            var raw = (float)(elapsedMs / entry.DurationMs);
            if (entry.Loop)
            {
                entry.Frame(raw - MathF.Floor(raw));
                continue;
            }

            var finished = raw >= 1f;
            entry.Frame(Ease(finished ? 1f : raw, entry.Easing));
            if (!finished) continue;

            Entries.RemoveAt(index);
            entry.Completed?.Invoke();
        }

        if (Entries.Count == 0) Clock.Stop();
    }

    private static bool ClientAreaAnimationEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && enabled;
        }
        catch
        {
            return true;
        }
    }

    private sealed class MotionEntry : IDisposable
    {
        public WeakReference<Control> Owner { get; }
        public long StartTimestamp { get; } = Stopwatch.GetTimestamp();
        public int DurationMs { get; }
        public int DelayMs { get; }
        public MotionEasing Easing { get; }
        public Action<float> Frame { get; }
        public Action? Completed { get; }
        public bool Loop { get; }
        public bool Cancelled { get; private set; }

        public MotionEntry(Control owner, int durationMs, int delayMs, MotionEasing easing, Action<float> frame, Action? completed, bool loop)
        {
            Owner = new WeakReference<Control>(owner);
            DurationMs = durationMs;
            DelayMs = Math.Max(0, delayMs);
            Easing = easing;
            Frame = frame;
            Completed = completed;
            Loop = loop;
        }

        public void Dispose() => Cancelled = true;
    }

    private sealed class EmptyHandle : IDisposable
    {
        public static readonly EmptyHandle Instance = new();
        public void Dispose() { }
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value,
        uint update);
}

public sealed class ShimmerBar : Control
{
    private float progress;
    private IDisposable? motion;

    public int Radius { get; set; } = 5;

    public ShimmerBar()
    {
        Height = 10;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Loading";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StartMotion();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated) StartMotion();
    }

    private void StartMotion()
    {
        motion?.Dispose();
        motion = MotionClock.Loop(this, 1250, value => { progress = value; Invalidate(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Visuals.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0, 0, Width, Height);
        using var baseBrush = new SolidBrush(Visuals.SurfaceRaised);
        e.Graphics.FillRoundedRectangle(baseBrush, bounds, this.Dp(Radius));
        if (MotionClock.IsReduced) return;

        var highlightWidth = Math.Max(this.Dp(44), Width * .28f);
        var center = -highlightWidth + (Width + highlightWidth * 2) * progress;
        using var gradient = new LinearGradientBrush(
            new PointF(center - highlightWidth, 0),
            new PointF(center + highlightWidth, 0),
            Color.Transparent,
            Color.Transparent);
        gradient.InterpolationColors = new ColorBlend
        {
            Colors = [Color.Transparent, Color.FromArgb(44, Visuals.TextSecondary), Color.Transparent],
            Positions = [0f, .5f, 1f],
        };
        using var path = Visuals.RoundedPath(bounds, this.Dp(Radius));
        e.Graphics.FillPath(gradient, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) motion?.Dispose();
        base.Dispose(disposing);
    }
}
