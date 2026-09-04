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
