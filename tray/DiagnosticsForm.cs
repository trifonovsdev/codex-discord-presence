namespace CodexPresence;

public sealed class DiagnosticsForm : ModernForm
{
    private readonly DiagnosticsService diagnostics;
    private readonly StatusPill summary = new() { Text = "Running checks", DotColor = Visuals.Muted, FillColor = Visuals.Background };
    private readonly BufferedFlowLayoutPanel rows = new()
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
        CloseOnEscape = true;
        StartPosition = FormStartPosition.CenterParent;

        var header = new Panel { Dock = DockStyle.Top, BackColor = Visuals.Background };
        var title = Visuals.Heading("System health", 20);
        var subtitle = Visuals.Label("A local readiness check for Codex, Discord, hooks, and SSH.", 9, true);
        subtitle.AutoSize = false;
        subtitle.AutoEllipsis = true;
        summary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([title, subtitle, summary]);

        rows.Resize += (_, _) => ResizeRows();

        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = Visuals.Canvas };
        var divider = new Panel { Dock = DockStyle.Top, BackColor = Visuals.BorderSoft };
        rerun.Click += async (_, _) => await RunAsync();
        copy.Enabled = false;
        copy.Click += (_, _) => CopyReport();
        var close = Visuals.Button("Close", ButtonKind.Ghost);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => Close();
        footer.Controls.AddRange([divider, rerun, copy, close]);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Visuals.Background };
        body.Controls.Add(rows);
        ContentHost.Controls.Add(body);
        ContentHost.Controls.Add(footer);
        ContentHost.Controls.Add(header);

        void LayoutHeader()
        {
            var horizontalInset = header.Dp(28);
            title.Location = new Point(horizontalInset, header.Dp(22));
            summary.Location = new Point(header.Width - summary.Width - horizontalInset, header.Dp(34));
            subtitle.SetBounds(
                horizontalInset + header.Dp(1),
                header.Dp(56),
                Math.Max(header.Dp(220), summary.Left - horizontalInset * 2),
                header.Dp(24));
        }

        void LayoutFooter()
        {
            var horizontalInset = footer.Dp(24);
            var buttonTop = footer.Dp(15);
            var buttonHeight = footer.Dp(42);
            rerun.SetBounds(horizontalInset, buttonTop, footer.Dp(138), buttonHeight);
            copy.SetBounds(rerun.Right + footer.Dp(10), buttonTop, footer.Dp(146), buttonHeight);
            close.SetBounds(footer.Width - footer.Dp(96) - horizontalInset, buttonTop, footer.Dp(96), buttonHeight);
        }

        void ApplyDpiMetrics()
        {
            MinimumSize = new Size(this.Dp(700), this.Dp(520));
            header.Height = this.Dp(100);
            footer.Height = this.Dp(72);
            divider.Height = Math.Max(1, this.Dp(1));
            summary.Height = this.Dp(28);
            body.Padding = new Padding(this.Dp(28), 0, this.Dp(28), this.Dp(16));
            rows.Padding = new Padding(0, 0, this.Dp(8), 0);
            LayoutHeader();
            LayoutFooter();
            ResizeRows();
        }

        header.Resize += (_, _) => LayoutHeader();
        footer.Resize += (_, _) => LayoutFooter();
        HandleCreated += (_, _) => ApplyDpiMetrics();
        DpiChanged += (_, _) => ApplyDpiMetrics();
        ApplyDpiMetrics();
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
        summary.FillColor = Visuals.Background;
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
            delay = Math.Min(delay + 32, 192);
        }
        rows.ResumeLayout();
        ResizeRows();

        var passed = results.Count(item => item.Passed);
        var hasResults = results.Count > 0;
        var allPassed = hasResults && passed == results.Count;
        summary.Text = !hasResults ? "No checks returned" : allPassed ? $"All {passed} checks passed" : $"{passed} of {results.Count} passed";
        summary.DotColor = !hasResults ? Visuals.Muted : allPassed ? Visuals.Success : Visuals.Danger;
        summary.FillColor = Visuals.Background;
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
            Radius = 0,
            BorderWidth = 0,
            BackColor = Visuals.Background,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = text,
        };
        row.Paint += (_, e) =>
        {
            using var rule = new Pen(Visuals.BorderSoft, Math.Max(1f, row.Scale()));
            e.Graphics.DrawLine(rule, 0, row.Height - rule.Width / 2f, row.Width, row.Height - rule.Width / 2f);
        };
        var icon = new ShimmerBar { Radius = 9 };
        var title = new ShimmerBar();
        var detail = new ShimmerBar { Radius = 4 };
        row.Controls.AddRange([icon, title, detail]);

        void LayoutSkeleton()
        {
            var targetHeight = row.Dp(68);
            if (row.Height != targetHeight) row.Height = targetHeight;
            icon.SetBounds(row.Dp(18), row.Dp(18), row.Dp(18), row.Dp(18));
            title.SetBounds(row.Dp(50), row.Dp(18), row.Dp(148), row.Dp(10));
            detail.SetBounds(
                row.Dp(50),
                row.Dp(39),
                Math.Max(row.Dp(80), Math.Min(row.Dp(286), row.Width - row.Dp(68))),
                row.Dp(8));
        }

        row.HandleCreated += (_, _) => LayoutSkeleton();
        row.DpiChangedAfterParent += (_, _) => LayoutSkeleton();
        row.Resize += (_, _) => LayoutSkeleton();
        LayoutSkeleton();
        return row;
    }

    private void ResizeRows()
    {
        var scrollbarWidth = rows.VerticalScroll.Visible ? rows.Dp(18) : 0;
        var width = Math.Max(rows.Dp(400), rows.ClientSize.Width - rows.Padding.Horizontal - scrollbarWidth);
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
        private IDisposable? revealMotion;
        private float reveal = 1f;

        public DiagnosticResultRow(DiagnosticItem result)
        {
            this.result = result;
            Radius = 0;
            BorderWidth = 0;
            BackColor = result.Passed ? Visuals.Background : Visuals.DangerSurface;
            Margin = Padding.Empty;
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleName = $"{result.Name}: {(result.Passed ? "passed" : "needs attention")}. {result.Detail}";
            ApplyDpiMetrics();
        }

        public void BeginReveal(int delayMs)
        {
            revealMotion?.Dispose();
            if (MotionClock.IsReduced)
            {
                reveal = 1f;
                Invalidate();
                return;
            }

            reveal = 0f;
            Invalidate();
            revealMotion = MotionClock.Animate(this, 170, value =>
            {
                reveal = value;
                Invalidate();
            }, MotionEasing.EaseOutCubic, delayMs, completed: () => revealMotion = null);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var baseColor = BackColor;
            var statusColor = result.Passed ? Visuals.Success : Visuals.Danger;
            var foreground = Visuals.Blend(baseColor, Visuals.Text, reveal);
            var secondary = Visuals.Blend(baseColor, Visuals.TextSecondary, reveal);
            var revealedStatus = Visuals.Blend(baseColor, statusColor, reveal);
            var offset = (int)Math.Round(this.Dp(7) * (1f - reveal));

            var iconBounds = new RectangleF(this.Dp(16) + offset, this.Dp(21), this.Dp(20), this.Dp(20));
            UiIcons.Draw(e.Graphics, result.Passed ? UiIcon.Check : UiIcon.Warning, iconBounds, revealedStatus);

            var stateText = result.Passed ? "Ready" : "Check";
            var stateFont = Visuals.Font(8, FontStyle.Bold);
            var measuredState = TextRenderer.MeasureText(
                e.Graphics,
                stateText,
                stateFont,
                Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            var stateWidth = Math.Max(this.Dp(48), measuredState.Width);
            var stateBounds = new Rectangle(
                Width - stateWidth - this.Dp(18),
                this.Dp(21),
                stateWidth,
                this.Dp(21));
            TextRenderer.DrawText(
                e.Graphics,
                stateText,
                stateFont,
                stateBounds,
                revealedStatus,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            var textLeft = this.Dp(46) + offset;
            var textRight = stateBounds.Left - this.Dp(12);
            var textWidth = Math.Max(0, textRight - textLeft);
            var textFlags = TextFormatFlags.Left |
                            TextFormatFlags.VerticalCenter |
                            TextFormatFlags.EndEllipsis |
                            TextFormatFlags.NoPrefix |
                            TextFormatFlags.SingleLine;
            TextRenderer.DrawText(
                e.Graphics,
                result.Name,
                Visuals.Font(9.5f, FontStyle.Bold),
                new Rectangle(textLeft, this.Dp(9), textWidth, this.Dp(23)),
                foreground,
                textFlags);
            TextRenderer.DrawText(
                e.Graphics,
                result.Detail,
                Visuals.Font(8.25f),
                new Rectangle(textLeft, this.Dp(33), textWidth, this.Dp(22)),
                secondary,
                textFlags);

            using var rule = new Pen(result.Passed ? Visuals.BorderSoft : Visuals.Danger, Math.Max(1f, this.Scale()));
            e.Graphics.DrawLine(rule, 0, Height - rule.Width / 2f, Width, Height - rule.Width / 2f);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiMetrics();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiMetrics();
        }

        private void ApplyDpiMetrics()
        {
            var targetHeight = this.Dp(64);
            if (Height != targetHeight) Height = targetHeight;
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
