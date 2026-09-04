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
Check(PresencePresentation.Create(null, privacy, now).PreviewLabel == "Not published", "offline is not published");
