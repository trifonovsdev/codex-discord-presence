using System.Drawing.Drawing2D;

namespace CodexPresence;

/// <summary>A local preview of the fields Discord receives from the daemon.</summary>
public sealed class DiscordCardPreview : RoundedPanel
{
    private string? projectName;
    private string? taskTitle;
    private string? fileName;
    private string elapsed = "00:00:00";
    private string language = "en";
    private string fileMode = "relative";
    private string? publishError;
    private bool showProject = true;
    private bool showTaskTitle = true;
    private bool showFile = true;
    private bool showTimer = true;
    private bool connected;
    private bool published = true;
    private bool hasTimestamp = true;

    public string? ProjectName { get => projectName; set { projectName = value; RefreshPreview(); } }
    public string? TaskTitle { get => taskTitle; set { taskTitle = value; RefreshPreview(); } }
    public string? FileName { get => fileName; set { fileName = value; RefreshPreview(); } }
    public string Elapsed { get => elapsed; set { elapsed = value; RefreshPreview(); } }
    public string Language { get => language; set { language = value; RefreshPreview(); } }
    public string FileMode { get => fileMode; set { fileMode = value; RefreshPreview(); } }
    public string? PublishError { get => publishError; set { publishError = value; RefreshPreview(); } }
    public bool ShowProject { get => showProject; set { showProject = value; RefreshPreview(); } }
    public bool ShowTaskTitle { get => showTaskTitle; set { showTaskTitle = value; RefreshPreview(); } }
    public bool ShowFile { get => showFile; set { showFile = value; RefreshPreview(); } }
    public bool ShowTimer { get => showTimer; set { showTimer = value; RefreshPreview(); } }
    public bool Connected { get => connected; set { connected = value; RefreshPreview(); } }
    public bool Published { get => published; set { published = value; RefreshPreview(); } }
    public bool HasTimestamp { get => hasTimestamp; set { hasTimestamp = value; RefreshPreview(); } }

    public DiscordCardPreview()
    {
        Height = 188;
        Radius = 14;
        BackColor = Visuals.Surface;
        BorderColor = Visuals.BorderSoft;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Discord card preview";
        SetStyle(ControlStyles.ResizeRedraw, true);
        RefreshPreview();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var padding = this.Dp(16);
        TextRenderer.DrawText(e.Graphics, "Discord preview", Visuals.Font(8.5f, FontStyle.Bold),
            new Rectangle(padding, this.Dp(12), Width - padding * 2, this.Dp(22)), Visuals.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var cardHeight = Math.Min(this.Dp(128), Math.Max(this.Dp(102), Height - this.Dp(58)));
        var card = new RectangleF(padding, this.Dp(42), Math.Max(80, Width - padding * 2), cardHeight);
        using var cardFill = new SolidBrush(Color.FromArgb(39, 42, 49));
        using var cardBorder = new Pen(Color.FromArgb(57, 61, 70));
        e.Graphics.FillRoundedRectangle(cardFill, card, this.Dp(11));
        e.Graphics.DrawRoundedRectangle(cardBorder, RectangleF.Inflate(card, -.5f, -.5f), this.Dp(11));

        if (!published)
        {
            var failed = !string.IsNullOrWhiteSpace(publishError);
            var statusTitle = failed ? "Discord rejected update" : connected ? "Publishing…" : "Not published";
            var statusDetail = failed
                ? publishError!
                : connected
                ? "Waiting for Discord to acknowledge the current presence update."
                : "Discord receives no activity while presence is paused or unavailable.";
            var statusIcon = new RectangleF(card.X + this.Dp(14), card.Y + this.Dp(18), this.Dp(22), this.Dp(22));
            UiIcons.Draw(e.Graphics, failed ? UiIcon.Warning : UiIcon.Info, statusIcon, failed ? Visuals.Danger : Visuals.Muted);
            var statusLeft = (int)statusIcon.Right + this.Dp(11);
            TextRenderer.DrawText(e.Graphics, statusTitle, Visuals.Font(9.25f, FontStyle.Bold),
                new Rectangle(statusLeft, (int)card.Y + this.Dp(14), (int)card.Right - statusLeft - this.Dp(12), this.Dp(22)),
                failed ? Visuals.Danger : Visuals.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, statusDetail, Visuals.Font(8f),
                new Rectangle(statusLeft, (int)card.Y + this.Dp(39), (int)card.Right - statusLeft - this.Dp(12), this.Dp(52)),
                Visuals.Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var iconSize = Math.Min(this.Dp(50), (int)card.Height - this.Dp(28));
        var iconBounds = new RectangleF(card.X + this.Dp(12), card.Y + this.Dp(13), iconSize, iconSize);
        using var iconFill = new SolidBrush(Visuals.Canvas);
        e.Graphics.FillRoundedRectangle(iconFill, iconBounds, this.Dp(12));
        var brandBounds = RectangleF.Inflate(iconBounds, -this.Dp(9), -this.Dp(9));
        UiIcons.Draw(e.Graphics, UiIcon.Brand, brandBounds, Visuals.Text, 1.6f);
        using var liveDot = new SolidBrush(connected ? Visuals.Success : Visuals.Muted);
        var liveSize = this.Dp(7);
        e.Graphics.FillEllipse(
            liveDot,
            brandBounds.Left + brandBounds.Width * .83f - liveSize / 2f,
            brandBounds.Top + brandBounds.Height * .5f - liveSize / 2f,
            liveSize,
            liveSize);

        var textLeft = (int)iconBounds.Right + this.Dp(12);
        var textWidth = Math.Max(this.Dp(80), (int)card.Right - textLeft - this.Dp(12));
        TextRenderer.DrawText(e.Graphics, "Coding with Codex", Visuals.Font(9.5f, FontStyle.Bold),
            new Rectangle(textLeft, (int)card.Y + this.Dp(11), textWidth, this.Dp(21)), Color.FromArgb(239, 240, 242),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, PrimaryLine(), Visuals.Font(8.5f, FontStyle.Bold),
            new Rectangle(textLeft, (int)card.Y + this.Dp(35), textWidth, this.Dp(20)), Color.FromArgb(214, 216, 221),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, SecondaryLine(), Visuals.Font(8f),
            new Rectangle(textLeft, (int)card.Y + this.Dp(56), textWidth, this.Dp(20)), Color.FromArgb(177, 181, 190),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (showTimer && hasTimestamp && card.Height >= this.Dp(104))
        {
            var timerTop = (int)card.Bottom - this.Dp(29);
            using var divider = new Pen(Color.FromArgb(52, 56, 65));
            e.Graphics.DrawLine(divider, card.X + this.Dp(12), timerTop - this.Dp(5), card.Right - this.Dp(12), timerTop - this.Dp(5));
            using var timerDot = new SolidBrush(connected ? Visuals.Success : Visuals.Muted);
            e.Graphics.FillEllipse(timerDot, card.X + this.Dp(13), timerTop + this.Dp(5), this.Dp(6), this.Dp(6));
            TextRenderer.DrawText(e.Graphics, elapsed, Visuals.MonoFont(8.5f, FontStyle.Bold),
                new Rectangle((int)card.X + this.Dp(26), timerTop, (int)card.Width - this.Dp(38), this.Dp(18)),
                connected ? Visuals.Success : Visuals.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        if (Height - card.Bottom >= this.Dp(48))
        {
            var noteTop = (int)card.Bottom + this.Dp(17);
            UiIcons.Draw(e.Graphics, UiIcon.Info, new RectangleF(padding, noteTop, this.Dp(17), this.Dp(17)), Visuals.Muted);
            TextRenderer.DrawText(e.Graphics, "Mirrors the exact text and visibility sent by the local service.", Visuals.Font(8f),
                new Rectangle(padding + this.Dp(27), noteTop - this.Dp(2), Width - padding * 2 - this.Dp(27), this.Dp(40)),
                Visuals.Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    private string PrimaryLine()
    {
        var russian = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase);
        if (showProject && !string.IsNullOrWhiteSpace(projectName))
            return russian ? $"Проект: {projectName}" : $"Project: {projectName}";
        if (showTaskTitle && !string.IsNullOrWhiteSpace(taskTitle))
            return russian ? $"Задача: {taskTitle}" : $"Task: {taskTitle}";
        if (!showProject) return "Codex Desktop";
        return russian ? "Работает в Codex" : "Working in Codex";
    }

    private string SecondaryLine()
    {
        var russian = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase);
        if (!showFile) return russian ? "Работает приватно" : "Working privately";
        if (string.IsNullOrWhiteSpace(fileName)) return russian ? "Активная сессия Codex" : "Active Codex session";
        var value = string.Equals(fileMode, "name", StringComparison.OrdinalIgnoreCase)
            ? fileName.Replace('\\', '/').Split('/').LastOrDefault() ?? fileName
            : fileName;
        return russian ? $"Файл: {value}" : $"Editing: {value}";
    }

    private void RefreshPreview()
    {
        AccessibleDescription = published
            ? $"Coding with Codex. {PrimaryLine()}. {SecondaryLine()}."
            : !string.IsNullOrWhiteSpace(publishError)
                ? $"Discord preview. Discord rejected the current update: {publishError}"
            : connected
                ? "Discord preview. The current presence update is waiting for Discord acknowledgement."
                : "Discord preview. Activity is not currently published.";
        Invalidate();
    }
}
