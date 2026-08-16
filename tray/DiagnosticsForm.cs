namespace CodexPresence;

public sealed class DiagnosticsForm : ModernForm
{
    private readonly DiagnosticsService diagnostics;
    private readonly StatusPill summary = new() { Text = "Running checks", DotColor = Visuals.Muted, FillColor = Visuals.SurfaceRaised };
    private readonly FlowLayoutPanel rows = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Visuals.Background,
        Padding = new Padding(0, 0, 8, 0),
        Margin = new Padding(0),
    };
    private readonly ModernButton rerun = Visuals.Button("Run again", ButtonKind.Primary, UiIcon.Refresh);
    private readonly ModernButton copy = Visuals.Button("Copy report", ButtonKind.Secondary, UiIcon.Copy);
    private List<DiagnosticItem> results = [];
    private bool running;
    private CancellationTokenSource? runCancellation;

    public DiagnosticsForm(DiagnosticsService diagnostics) : base("Doctor", new Size(860, 760), resizable: true)
    {
        this.diagnostics = diagnostics;
        MinimumSize = new Size(700, 520);
        CloseOnEscape = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Visuals.Background };
        var title = Visuals.Heading("System health", 20);
        title.Location = new Point(28, 22);
        var subtitle = Visuals.Label("A local readiness check for Codex, Discord, hooks, and SSH.", 9, true);
        subtitle.Location = new Point(29, 58);
        summary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([title, subtitle, summary]);
        header.Resize += (_, _) => summary.Location = new Point(header.Width - summary.Width - 28, 34);

        rows.Resize += (_, _) => ResizeRows();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Visuals.Canvas };
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft };
        rerun.SetBounds(24, 15, 138, 42);
        rerun.Click += async (_, _) => await RunAsync();
        copy.SetBounds(172, 15, 146, 42);
        copy.Enabled = false;
        copy.Click += (_, _) => CopyReport();
        var close = Visuals.Button("Close", ButtonKind.Ghost);
        close.SetBounds(0, 15, 96, 42);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => Close();
        footer.Controls.AddRange([divider, rerun, copy, close]);
        footer.Resize += (_, _) => close.Left = footer.Width - close.Width - 24;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 0, 28, 16), BackColor = Visuals.Background };
        body.Controls.Add(rows);
        ContentHost.Controls.Add(body);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(header);
        FormClosing += (_, _) => runCancellation?.Cancel();
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        if (running) return;
        running = true;
        rerun.Enabled = false;
        copy.Enabled = false;
        summary.Text = "Running checks";
        summary.DotColor = Visuals.Muted;
        summary.FillColor = Visuals.SurfaceRaised;
        summary.IsLive = true;
        rows.SuspendLayout();
        DisposeRows();
        rows.Controls.Add(SkeletonRow("Inspecting local services…"));
        rows.ResumeLayout();
        UseWaitCursor = true;
        runCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        var cancelled = false;

        try
        {
            results = await diagnostics.RunAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            results = [new DiagnosticItem("Doctor", false, error.Message)];
        }
        finally
        {
            cancelled = cancellation.IsCancellationRequested;
            if (ReferenceEquals(runCancellation, cancellation)) runCancellation = null;
            cancellation.Dispose();
            if (!IsDisposed && !Disposing)
            {
                UseWaitCursor = false;
                running = false;
                rerun.Enabled = true;
            }
        }

        if (IsDisposed || Disposing || cancelled) return;

        rows.SuspendLayout();
        DisposeRows();
        var delay = 0;
        foreach (var result in results)
        {
            var row = new DiagnosticResultRow(result);
            rows.Controls.Add(row);
            row.BeginReveal(delay);
            delay += 32;
        }
        rows.ResumeLayout();
        ResizeRows();

        var passed = results.Count(item => item.Passed);
        var allPassed = passed == results.Count;
        summary.Text = allPassed ? $"All {passed} checks passed" : $"{passed} of {results.Count} passed";
        summary.DotColor = allPassed ? Visuals.Success : Visuals.Danger;
        summary.FillColor = allPassed ? Visuals.SuccessSurface : Visuals.DangerSurface;
        summary.IsLive = allPassed;
        copy.Enabled = results.Count > 0;
    }

    private void DisposeRows()
    {
        foreach (Control row in rows.Controls.Cast<Control>().ToList()) row.Dispose();
        rows.Controls.Clear();
    }

    private static RoundedPanel SkeletonRow(string text)
    {
        var row = new RoundedPanel
        {
            Height = 68,
            Radius = 11,
            BackColor = Visuals.Surface,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = text,
        };
        var icon = new ShimmerBar { Location = new Point(18, 18), Size = new Size(18, 18), Radius = 9 };
        var title = new ShimmerBar { Location = new Point(50, 18), Size = new Size(148, 10) };
        var detail = new ShimmerBar { Location = new Point(50, 39), Size = new Size(286, 8), Radius = 4 };
        row.Controls.AddRange([icon, title, detail]);
        return row;
    }

    private void ResizeRows()
    {
        var width = Math.Max(400, rows.ClientSize.Width - rows.Padding.Horizontal);
        foreach (Control row in rows.Controls) row.Width = width;
    }

    private void CopyReport()
    {
        try { Clipboard.SetText(BuildReport()); }
        catch (Exception error) { ModernDialog.Show(this, "Could not copy the report", error.Message, false); }
    }

    private string BuildReport() => string.Join(Environment.NewLine,
        ["Codex Presence Doctor", .. results.Select(item => $"[{(item.Passed ? "PASS" : "FAIL")}] {item.Name}: {item.Detail}")]);

    private sealed class DiagnosticResultRow : RoundedPanel
    {
        private readonly DiagnosticItem result;
        private readonly IconView icon;
        private readonly Label name;
        private readonly Label detail;
        private readonly Label state;
        private IDisposable? revealMotion;
        private float reveal = 1f;

        public DiagnosticResultRow(DiagnosticItem result)
        {
            this.result = result;
            Height = 60;
            Radius = 11;
            BackColor = Visuals.Surface;
            BorderColor = result.Passed ? Visuals.BorderSoft : Visuals.Danger;
            Margin = new Padding(0, 0, 0, 8);
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = $"{result.Name}: {(result.Passed ? "passed" : "needs attention")}. {result.Detail}";

            icon = new IconView(result.Passed ? UiIcon.Check : UiIcon.Warning)
            {
                Size = new Size(20, 20),
                IconColor = result.Passed ? Visuals.Success : Visuals.Danger,
            };
            name = Visuals.Label(result.Name, 9.5f, false, FontStyle.Bold);
            name.AutoSize = false;
            name.AutoEllipsis = true;
            name.Height = 20;
            detail = Visuals.Label(result.Detail, 8.25f, true);
            detail.AutoSize = false;
            detail.AutoEllipsis = true;
            detail.Height = 18;
            state = Visuals.Label(result.Passed ? "Ready" : "Check", 8, false, FontStyle.Bold);
            state.ForeColor = result.Passed ? Visuals.Success : Visuals.Danger;
            state.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.AddRange([icon, name, detail, state]);
            Resize += (_, _) => LayoutRow();
            LayoutRow();
        }

        public void BeginReveal(int delayMs)
        {
            revealMotion?.Dispose();
            reveal = MotionClock.IsReduced ? 1f : 0f;
            ApplyReveal();
            revealMotion = MotionClock.Animate(this, 170, value =>
            {
                reveal = value;
                ApplyReveal();
            }, MotionEasing.EaseOutCubic, delayMs);
        }

        private void LayoutRow()
        {
            var offset = (int)Math.Round(this.Dp(7) * (1f - reveal));
            icon.Location = new Point(this.Dp(16) + offset, this.Dp(20));
            name.Location = new Point(this.Dp(46) + offset, this.Dp(12));
            detail.Location = new Point(this.Dp(46) + offset, this.Dp(34));
            state.Location = new Point(Width - state.Width - this.Dp(18), this.Dp(21));
            var textWidth = Math.Max(this.Dp(160), Width - state.Width - this.Dp(88));
            name.Width = textWidth;
            detail.Width = textWidth;
        }

        private void ApplyReveal()
        {
            var baseColor = BackColor;
            icon.IconColor = Visuals.Blend(baseColor, result.Passed ? Visuals.Success : Visuals.Danger, reveal);
            name.ForeColor = Visuals.Blend(baseColor, Visuals.Text, reveal);
            detail.ForeColor = Visuals.Blend(baseColor, Visuals.TextSecondary, reveal);
            state.ForeColor = Visuals.Blend(baseColor, result.Passed ? Visuals.Success : Visuals.Danger, reveal);
            LayoutRow();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) revealMotion?.Dispose();
            base.Dispose(disposing);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            runCancellation?.Cancel();
        }
        base.Dispose(disposing);
    }
}
