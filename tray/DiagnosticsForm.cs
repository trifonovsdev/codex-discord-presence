namespace CodexPresence;

public sealed class DiagnosticsForm : ModernForm
{
    private readonly DiagnosticsService diagnostics;
    private readonly StatusPill summary = new() { Text = "Running checks", DotColor = Visuals.Muted, FillColor = Visuals.SurfaceRaised };
    private readonly FlowLayoutPanel rows = new()
    {
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Visuals.Background,
        Padding = new Padding(0, 0, 8, 0),
    };
    private List<DiagnosticItem> results = [];

    public DiagnosticsForm(DiagnosticsService diagnostics) : base("Doctor", new Size(860, 760))
    {
        this.diagnostics = diagnostics;
        MinimumSize = new Size(760, 650);

        var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Visuals.Background };
        var title = Visuals.Label("System health", 20, false, FontStyle.Bold); title.Location = new Point(28, 22);
        var subtitle = Visuals.Label("A local readiness check for Codex, Discord, hooks, and SSH.", 9, true); subtitle.Location = new Point(29, 58);
        summary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.AddRange([title, subtitle, summary]);
        header.Resize += (_, _) => summary.Location = new Point(header.Width - summary.Width - 28, 34);

        rows.Dock = DockStyle.Fill;
        rows.Margin = new Padding(0);
        rows.Resize += (_, _) => ResizeRows();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Visuals.Canvas };
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Visuals.BorderSoft };
        var rerun = Visuals.Button("Run again", ButtonKind.Primary, "↻");
        rerun.SetBounds(24, 15, 132, 42);
        rerun.Click += async (_, _) => await RunAsync();
        var copy = Visuals.Button("Copy report", ButtonKind.Secondary, "□");
        copy.SetBounds(166, 15, 138, 42);
        copy.Click += (_, _) => { if (results.Count > 0) Clipboard.SetText(BuildReport()); };
        var close = Visuals.Button("Close", ButtonKind.Ghost);
        close.SetBounds(0, 15, 92, 42);
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
        summary.Text = "Running checks";
        summary.DotColor = Visuals.Muted;
        summary.FillColor = Visuals.SurfaceRaised;
        summary.Invalidate();
        rows.Controls.Clear();
        rows.Controls.Add(SkeletonRow("Inspecting local services…"));
        UseWaitCursor = true;
        results = await diagnostics.RunAsync();
        UseWaitCursor = false;
        rows.Controls.Clear();
        foreach (var result in results) rows.Controls.Add(ResultRow(result));
        ResizeRows();
        var passed = results.Count(item => item.Passed);
        summary.Text = passed == results.Count ? $"All {passed} checks passed" : $"{passed} of {results.Count} passed";
        summary.DotColor = passed == results.Count ? Visuals.Success : Visuals.Danger;
        summary.FillColor = passed == results.Count ? Visuals.SuccessSurface : Visuals.DangerSurface;
        summary.Width = passed == results.Count ? 154 : 138;
        summary.Invalidate();
    }

    private RoundedPanel ResultRow(DiagnosticItem result)
    {
        var row = new RoundedPanel { Height = 50, Radius = 11, BackColor = Visuals.Surface, Margin = new Padding(0, 0, 0, 8) };
        var icon = Visuals.Label(result.Passed ? "✓" : "!", 10, false, FontStyle.Bold);
        icon.ForeColor = result.Passed ? Visuals.Success : Visuals.Danger;
        icon.Location = new Point(16, 15);
        var name = Visuals.Label(result.Name, 9.5f, false, FontStyle.Bold); name.Location = new Point(44, 9);
        var detail = Visuals.Label(result.Detail, 8, true); detail.Location = new Point(44, 29); detail.MaximumSize = new Size(590, 18);
        var state = Visuals.Label(result.Passed ? "PASS" : "CHECK", 8, false, FontStyle.Bold);
        state.ForeColor = result.Passed ? Visuals.Success : Visuals.Danger;
        state.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.AddRange([icon, name, detail, state]);
        row.Resize += (_, _) => state.Location = new Point(row.Width - state.Width - 18, 17);
        return row;
    }

    private static RoundedPanel SkeletonRow(string text)
    {
        var row = new RoundedPanel { Height = 52, Radius = 11, BackColor = Visuals.Surface };
        var label = Visuals.Label(text, 9, true); label.Location = new Point(18, 18); row.Controls.Add(label);
        return row;
    }

    private void ResizeRows()
    {
        foreach (Control row in rows.Controls) row.Width = Math.Max(580, rows.ClientSize.Width - 10);
    }

    private string BuildReport() => "Codex Presence Doctor\r\n" + string.Join("\r\n", results.Select(item => $"[{(item.Passed ? "PASS" : "FAIL")}] {item.Name}: {item.Detail}"));
}
