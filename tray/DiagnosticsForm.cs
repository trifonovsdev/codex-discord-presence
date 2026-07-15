namespace CodexPresence;

public sealed class DiagnosticsForm : Form
{
    private readonly DiagnosticsService diagnostics;
    private readonly ListView list = new();
    private readonly Label summary = Visuals.Label("Running diagnostics…", 10, true);
    private List<DiagnosticItem> results = [];

    public DiagnosticsForm(DiagnosticsService diagnostics)
    {
        this.diagnostics = diagnostics;
        Text = "Codex Presence Doctor";
        Icon = Visuals.CreateIcon();
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(680, 460);
        BackColor = Visuals.Background;
        ForeColor = Visuals.Text;
        StartPosition = FormStartPosition.CenterScreen;

        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(22), BackColor = Visuals.Surface };
        var title = Visuals.Label("Installation doctor", 18, false, FontStyle.Bold); title.Location = new Point(22, 17);
        summary.Location = new Point(24, 55);
        header.Controls.AddRange([title, summary]);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Visuals.Background;
        list.ForeColor = Visuals.Text;
        list.Columns.Add("Check", 190);
        list.Columns.Add("Status", 90);
        list.Columns.Add("Details", 440);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Visuals.Surface, Padding = new Padding(18, 12, 18, 12) };
        var rerun = Visuals.Button("Run again", true); rerun.SetBounds(18, 12, 112, 38); rerun.Click += async (_, _) => await RunAsync();
        var copy = Visuals.Button("Copy report"); copy.SetBounds(140, 12, 118, 38); copy.Click += (_, _) => Clipboard.SetText(BuildReport());
        var close = Visuals.Button("Close"); close.SetBounds(0, 12, 90, 38); close.Click += (_, _) => Close();
        footer.Controls.AddRange([rerun, copy, close]);
        footer.Resize += (_, _) => close.Left = footer.ClientSize.Width - close.Width - 18;
        Controls.Add(list);
        Controls.Add(footer);
        Controls.Add(header);
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        summary.Text = "Running diagnostics…";
        summary.ForeColor = Visuals.Muted;
        list.Items.Clear();
        UseWaitCursor = true;
        results = await diagnostics.RunAsync();
        UseWaitCursor = false;
        foreach (var result in results)
        {
            var item = new ListViewItem(result.Name) { ForeColor = result.Passed ? Visuals.Success : Visuals.Danger };
            item.SubItems.Add(result.Passed ? "PASS" : "FAIL");
            item.SubItems.Add(result.Detail);
            list.Items.Add(item);
        }
        var passed = results.Count(item => item.Passed);
        summary.Text = $"{passed}/{results.Count} checks passed";
        summary.ForeColor = passed == results.Count ? Visuals.Success : Visuals.Danger;
    }

    private string BuildReport() => "Codex Presence Doctor\r\n" + string.Join("\r\n", results.Select(item => $"[{(item.Passed ? "PASS" : "FAIL")}] {item.Name}: {item.Detail}"));
}
