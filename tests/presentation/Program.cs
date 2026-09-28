using CodexPresence;

var now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
var privacy = new PrivacyConfig();
var snapshot = new HealthSnapshot
{
    PresenceEnabled = true, CodexRunning = true, RpcReady = true, RpcPublished = true,
    Project = "Presence", File = "tray/MainWindow.xaml", CodexStartedAt = now.AddMinutes(-8),
};
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}");
}
PresencePresentation Present() => PresencePresentation.Create(snapshot, privacy, now);
Check(Present().PreviewLabel == "Published", "acknowledged activity is published");
Check(Present().Session == "Elapsed 00:08:00", "elapsed time uses the session start");
snapshot.RpcPublished = false;
Check(Present().PreviewLabel == "Not published", "unacknowledged activity is not labeled live");
snapshot.PresenceEnabled = false;
Check(Present().PreviewLabel == "Not published" && !Present().ShowPreviewElapsed, "paused activity hides the public timer");
Check(Present().SharingSummary.StartsWith("Configured:"), "paused privacy settings are not described as currently shared");
snapshot.PresenceEnabled = true;
snapshot.RpcPublished = true;
privacy.ShowFile = false;
Check(!Present().PreviewSecondary.Contains("MainWindow"), "private file stays out of preview");
privacy.ShowTimer = false;
Check(!Present().ShowPreviewElapsed, "private timer stays out of preview");
snapshot.CodexStartedAt = now.AddHours(1);
Check(Present().Session == "Elapsed 00:00:00", "future timestamps clamp to zero");
snapshot.CodexRunning = false;
Check(Present().PreviewLabel == "Not published", "closed Codex does not look published");
var unknown = PresencePresentation.Create(null, privacy, now, "Local request timed out.");
Check(unknown.PreviewLabel == "Status unknown" && unknown.PreviewTone != PresenceTone.Danger, "failed health requests do not claim Discord is offline");
Check(!unknown.SharingSummary.StartsWith("Sharing") && !unknown.PauseEnabled, "unverified status does not claim to be sharing or enable controls");
snapshot.CodexRunning = true;
var stale = PresencePresentation.Create(snapshot, privacy, now, "Local request timed out.", now.AddMinutes(-1));
Check(stale.Project == "Presence" && stale.PreviewLabel == "Last confirmed", "last confirmed context survives a temporary connection failure");
Check(!stale.ShowPreviewElapsed && !stale.PreviewSecondary.Contains("MainWindow"), "stale preview freezes the public timer and respects privacy");
Check(Present().PreviewLabel == "Published" && Present().PauseEnabled, "successful reconnect restores live status and controls");
var privateFields = new PrivacyConfig { ShowProject = false, ShowFile = false, ShowTaskTitle = false, ShowTimer = false };
Check(PresencePresentation.Create(snapshot, privateFields, now).SharingSummary == "Sharing app name only", "hiding all optional fields still discloses the app name");

foreach (var start in new DateTimeOffset?[] { null, now.AddHours(1), now.AddDays(-2) })
{
    snapshot.CodexStartedAt = start;
    var timing = PresencePresentation.SessionTiming(snapshot, now);
    Check(timing.Session == Present().Session && timing.Elapsed == Present().PreviewElapsed,
        $"timer-only updates match the full projection for {start}");
}

// Claude Code support: agent-aware snapshots from the 2.6 service.
var claude = new HealthSnapshot
{
    PresenceEnabled = true, RpcReady = true, RpcPublished = true, CodexRunning = true,
    Agent = "claude", AgentLabel = "Claude Code", AgentRunning = true,
    Project = "storefront", File = "src/cart.ts", Source = "claude-transcript",
    StartedAt = now.AddMinutes(-3), CodexStartedAt = now.AddHours(-5),
    Activity = new PublishedActivity { Name = "Coding with Claude Code", Details = "Project: storefront", State = "Editing: src/cart.ts" },
    Agents = new AgentsHealth
    {
        Codex = new CodexHealth { Enabled = true, Running = true },
        Claude = new ClaudeHealth { Enabled = true, Installed = true, Active = true, Surface = "desktop", Sessions = 2 },
    },
};
privacy = new PrivacyConfig();
PresencePresentation PresentClaude() => PresencePresentation.Create(claude, privacy, now);
Check(PresentClaude().Agent == "claude" && PresentClaude().AgentName == "Claude Code", "the card owner is Claude Code");
Check(PresentClaude().Session == "Elapsed 00:03:00", "Claude Code uses its own run timer, not the Codex process start");
Check(PresentClaude().PreviewTitle == "Coding with Claude Code" && PresentClaude().PreviewSecondary == "Editing: src/cart.ts", "preview mirrors the published activity");
Check(PresentClaude().Source == "Claude Desktop", "desktop sessions are named after their surface");
Check(PresentClaude().ClaudeChip.State == AgentChipState.Owner && PresentClaude().CodexChip.State == AgentChipState.Active, "chips show the owner and the other open agent");
Check(PresentClaude().ClaudeChip.ToolTip.Contains("2 active sessions"), "the owner chip explains how many sessions are followed");

claude.Agent = null;
claude.AgentRunning = false;
claude.StartedAt = null;
claude.Agents.Codex.Running = false;
claude.Agents.Claude.Active = false;
Check(PresentClaude().Connection == "Waiting for an agent" && PresentClaude().PreviewLabel == "Not published", "idle agents publish nothing");
Check(PresentClaude().Project == "Waiting for an agent" && PresentClaude().CopyPath is null, "stale project context is not shown while idle");
Check(PresentClaude().Session == "No active session", "idle agents have no timer");
claude.Agents.Codex.Enabled = false;
Check(PresentClaude().Connection == "Waiting for Claude Code" && PresentClaude().PreviewAgent == "claude", "Claude-only setups wait for Claude Code");
Check(PresentClaude().CodexChip.State == AgentChipState.Disabled, "a disabled agent is shown as off");
var legacy = PresencePresentation.Create(new HealthSnapshot { PresenceEnabled = true, RpcReady = true, RpcPublished = true, CodexRunning = true, CodexStartedAt = now.AddMinutes(-1) }, privacy, now);
Check(legacy.Agent == "codex" && legacy.Session == "Elapsed 00:01:00" && legacy.ClaudeChip.State == AgentChipState.Disabled, "an older service still renders as Codex");
