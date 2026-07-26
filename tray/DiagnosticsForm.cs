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
    private readonly ModernButton rerun = Visuals.Button("Run again", ButtonKind.Primary, "↻");
    private readonly ModernButton copy = Visuals.Button("Copy report", ButtonKind.Secondary, "□");
    private List<DiagnosticItem> results = [];
    private bool running;

    public DiagnosticsForm(DiagnosticsService diagnostics) : base("Doctor", new Size(860, 760), resizable: true)
    {
        this.diagnostics = diagnostics;
        MinimumSize = new Size(700, 520);
        CloseOnEscape = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Visuals.Background };
        var title = Visuals.Label("System health", 20, false, FontStyle.Bold);
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
        rows.SuspendLayout();
        DisposeRows();
        rows.Controls.Add(SkeletonRow("Inspecting local services…"));
        rows.ResumeLayout();
        UseWaitCursor = true;

        try
        {
            results = await diagnostics.RunAsync();
        }
        catch (Exception error)
        {
            results = [new DiagnosticItem("Doctor", false, error.Message)];
        }
        finally
        {
            UseWaitCursor = false;
            running = false;
            rerun.Enabled = true;
        }

        rows.SuspendLayout();
        DisposeRows();
        foreach (var result in results) rows.Controls.Add(ResultRow(result));
        rows.ResumeLayout();
        ResizeRows();

        var passed = results.Count(item => item.Passed);
        var allPassed = passed == results.Count;
        summary.Text = allPassed ? $"All {passed} checks passed" : $"{passed} of {results.Count} passed";
        summary.DotColor = allPassed ? Visuals.Success : Visuals.Danger;
        summary.FillColor = allPassed ? Visuals.SuccessSurface : Visuals.DangerSurface;
        copy.Enabled = results.Count > 0;
    }

    private void DisposeRows()
    {
        foreach (Control row in rows.Controls.Cast<Control>().ToList()) row.Dispose();
        rows.Controls.Clear();
    }

    private static RoundedPanel ResultRow(DiagnosticItem result)
    {
        var row = new RoundedPanel
        {
            Height = 56,
            Radius = 11,
            BackColor = Visuals.Surface,
            // A failing check now reads as failing at a glance, not only via the trailing label.
            BorderColor = result.Passed ? Visuals.BorderSoft : Visuals.Danger,
            Margin = new Padding(0, 0, 0, 8),
        };

        var icon = Visuals.Label(result.Passed ? "✓" : "!", 10, false, FontStyle.Bold);
        icon.ForeColor = result.Passed ? Visuals.Success : Visuals.Danger;
        icon.Location = new Point(16, 18);
        var name = Visuals.Label(result.Name, 9.5f, false, FontStyle.Bold);
        name.Location = new Point(44, 12);
        var detail = Visuals.Label(result.Detail, 8, true);
        detail.Location = new Point(44, 32);
        detail.AutoEllipsis = true;
        var state = Visuals.Label(result.Passed ? "PASS" : "CHECK", 8, false, FontStyle.Bold);
        state.ForeColor = result.Passed ? Visuals.Success : Visuals.Danger;
        state.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        row.Controls.AddRange([icon, name, detail, state]);
        row.Resize += (_, _) =>
        {
            state.Location = new Point(row.Width - state.Width - 18, 20);
            detail.MaximumSize = new Size(Math.Max(160, row.Width - state.Width - 80), 0);
        };
        return row;
    }

    private static RoundedPanel SkeletonRow(string text)
    {
        var row = new RoundedPanel { Height = 56, Radius = 11, BackColor = Visuals.Surface };
        var label = Visuals.Label(text, 9, true);
        label.Location = new Point(18, 20);
        row.Controls.Add(label);
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
}
