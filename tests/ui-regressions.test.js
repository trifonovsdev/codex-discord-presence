const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const repository = path.resolve(__dirname, '..');
const source = (relativePath) => fs.readFileSync(path.join(repository, relativePath), 'utf8');

test('desktop shell uses unpackaged self-contained WinUI 3', () => {
  const project = source('tray/CodexPresence.Tray.csproj');
  const app = source('tray/App.xaml');

  assert.match(project, /<UseWinUI>true<\/UseWinUI>/);
  assert.match(project, /<WindowsPackageType>None<\/WindowsPackageType>/);
  assert.match(project, /<WindowsAppSDKSelfContained>true<\/WindowsAppSDKSelfContained>/);
  assert.match(project, /Microsoft\.WindowsAppSDK[^\n]+Version="2\.4\.0"/);
  assert.doesNotMatch(project, /<UseWindowsForms>true<\/UseWindowsForms>/);
  assert.match(app, /<ResourceDictionary\.MergedDictionaries>/);
  assert.match(app, /<XamlControlsResources\b/);
});

test('single-file runtime resolves payloads beside the launched executable', () => {
  const paths = source('tray/AppPaths.cs');
  const app = source('tray/App.xaml.cs');
  const tray = source('tray/TrayIcon.cs');

  assert.match(paths, /Environment\.ProcessPath/);
  assert.match(app, /AppPaths\.BaseDirectory/);
  assert.match(tray, /AppPaths\.BaseDirectory/);
});

test('app resources define one accessible graphite design system', () => {
  const app = source('tray/App.xaml');

  for (const token of [
    'CanvasBrush',
    'SurfaceBrush',
    'SurfaceRaisedBrush',
    'TextPrimaryBrush',
    'TextSecondaryBrush',
    'SuccessBrush',
    'DangerBrush',
    'FocusStrokeBrush',
    'PageTitleTextStyle',
    'BodyTextStyle',
  ]) {
    assert.match(app, new RegExp(`x:Key="${token}"`));
  }
  assert.match(app, /TargetType="Button"/);
  assert.match(app, /MinHeight[^\n]*44/);
});

test('custom surfaces follow Windows High Contrast colors', () => {
  const app = source('tray/App.xaml');

  assert.match(app, /<ResourceDictionary x:Key="Default">/);
  assert.match(app, /<ResourceDictionary x:Key="HighContrast">/);
  assert.match(app, /\{ThemeResource SystemColorWindowColor\}/);
  assert.match(app, /\{ThemeResource SystemColorWindowTextColor\}/);
  assert.match(app, /\{ThemeResource SystemColorHighlightColor\}/);
  assert.match(app, /\{ThemeResource SystemColorHighlightTextColor\}/);
  assert.match(app, /Value="\{ThemeResource TextPrimaryBrush\}"/);
});

test('code-assigned status brushes refresh when the Windows theme changes', () => {
  for (const windowCode of ['MainWindow.xaml.cs', 'SettingsWindow.xaml.cs', 'DiagnosticsWindow.xaml.cs']) {
    assert.match(source(`tray/${windowCode}`), /ActualThemeChanged/);
  }
});

test('dashboard is a focused Fluent surface with live state and essential actions', () => {
  const xaml = source('tray/MainWindow.xaml');
  const code = source('tray/MainWindow.xaml.cs');

  assert.match(xaml, /<TitleBar\b/);
  assert.match(code, /MicaBackdrop/);
  assert.match(xaml, /x:Name="ConnectionStatus"/);
  assert.match(xaml, /x:Name="ProjectName"/);
  assert.match(xaml, /x:Name="CurrentFile"/);
  assert.match(xaml, /x:Name="DiscordPreview"/);
  assert.match(xaml, /x:Name="PauseButton"/);
  assert.match(xaml, /AutomationProperties\.Name="Open settings"/);
  assert.match(xaml, /AutomationProperties\.Name="Run diagnostics"/);
  assert.match(code, /AppWindow\.Closing/);
  assert.match(code, /args\.Cancel\s*=\s*true/);
});

test('the compact shell uses the official Codex app artwork', () => {
  const xaml = source('tray/MainWindow.xaml');
  const code = source('tray/MainWindow.xaml.cs');
  const project = source('tray/CodexPresence.Tray.csproj');
  const icon = path.join(repository, 'assets', 'codex-app-icon.png');

  assert.match(code, /WindowSizing\.ResizeInDips\(this,\s*640,\s*454\)/);
  assert.match(xaml, /codex-app-icon\.png/);
  assert.match(xaml, /x:Name="PreviewIconViewport"/);
  assert.match(xaml, /Width="54"\s+Height="54"/);
  assert.match(project, /codex-app-icon\.png/);
  assert.ok(fs.statSync(icon).size > 20_000, 'the exact official artwork is bundled, not a placeholder glyph');
  assert.equal(
    crypto.createHash('sha256').update(fs.readFileSync(icon)).digest('hex'),
    '1c926e380bfe6a50f40648dd9bc5de88da7271546491adf99ec72172e17df6a0',
  );

  for (const window of ['MainWindow.xaml', 'SettingsWindow.xaml', 'DiagnosticsWindow.xaml']) {
    const titleBar = source(`tray/${window}`);
    assert.match(titleBar, /<TitleBar\.LeftHeader>/, `${window} must render the mark without its transparent padding`);
    assert.match(titleBar, /Width="24"\s+Height="24"/, `${window} must overscan the official source inside the clipped title icon`);
  }
});

test('activity title customization flows through config, health, settings, and preview', () => {
  const models = source('tray/Models.cs');
  const xaml = source('tray/SettingsWindow.xaml');
  const code = source('tray/SettingsWindow.xaml.cs');
  const preview = source('tray/PresencePresentation.cs');
  const diagnostics = source('tray/DiagnosticsService.cs');
  const paths = source('tray/AppPaths.cs');

  assert.match(models, /JsonPropertyName\("activityName"\)/);
  assert.match(xaml, /x:Name="ActivityNameInput"/);
  assert.match(xaml, /MaxLength="128"/);
  assert.match(code, /ActivityNameInput\.Text\s*=\s*config\.ActivityName/);
  assert.match(code, /ActivityName\s*=\s*activityName/);
  assert.match(preview, /snapshot\.ActivityName/);
  assert.match(models, /JsonPropertyName\("rpcTransport"\)/);
  assert.match(paths, /SocialSdkPath/);
  assert.match(diagnostics, /Discord Social SDK/);
  assert.match(diagnostics, /RpcTransport/);
});

test('release hosts activity-name publishing in an isolated Social SDK bridge', () => {
  const bridgePath = path.join(repository, 'tray', 'DiscordBridge.cs');
  const nativePath = path.join(repository, 'tray', 'DiscordSocialNative.cs');
  assert.ok(fs.existsSync(bridgePath), 'the native Social SDK bridge must be implemented');
  assert.ok(fs.existsSync(nativePath), 'the reviewed C ABI bindings must be implemented');

  const bridge = source('tray/DiscordBridge.cs');
  const native = source('tray/DiscordSocialNative.cs');
  const app = source('tray/App.xaml.cs');
  const build = source('build-release.ps1');
  const installer = source('installer/CodexPresence.iss');

  assert.match(app, /--discord-bridge/);
  assert.match(bridge, /ActivityName/);
  assert.match(bridge, /UpdateRichPresence/);
  assert.match(bridge, /Discord Desktop is not reachable/);
  assert.match(bridge, /Task\.Run\(ReadInputLoop\)/);
  assert.doesNotMatch(bridge, /Console\.In\.ReadLineAsync/);
  assert.match(native, /Discord_Activity_SetName/);
  assert.match(native, /Discord_Client_UpdateRichPresence/);
  assert.match(build, /DiscordSdkSha256/);
  assert.match(build, /discord_partner_sdk\.dll/);
  assert.match(build, /Get-FileHash/);
  assert.match(installer, /discord_partner_sdk\.dll/);
});

test('micro-interactions keep button hitboxes fixed and respect Windows animation settings', () => {
  const motion = source('tray/Motion.cs');
  const main = source('tray/MainWindow.xaml.cs');
  const settings = source('tray/SettingsWindow.xaml.cs');

  assert.match(motion, /AnimationsEnabled/);
  assert.match(motion, /CreateCubicBezierEasingFunction/);
  assert.match(motion, /StartAnimation\("Opacity"/);
  assert.match(motion, /PointerPressed/);
  assert.match(motion, /PointerReleased/);
  assert.doesNotMatch(motion, /PointerEntered|PointerExited|CenterPoint|Scale/);
  assert.doesNotMatch(motion, /Spring|Bounce|AutoReverse/);
  assert.match(main, /Motion\.AttachButtonFeedback/);
  assert.match(settings, /Motion\.AttachButtonFeedback/);
});

test('WinUI windows size in logical pixels on high-DPI displays', () => {
  const sizing = source('tray/WindowSizing.cs');

  assert.match(sizing, /GetDpiForWindow/);
  assert.match(sizing, /DipsToPixels/);
  for (const windowCode of ['MainWindow.xaml.cs', 'SettingsWindow.xaml.cs', 'DiagnosticsWindow.xaml.cs']) {
    assert.match(source(`tray/${windowCode}`), /WindowSizing\.ResizeInDips\(this,/);
  }
});

test('settings use a compact top navigation and preserve all configuration surfaces', () => {
  const xaml = source('tray/SettingsWindow.xaml');
  const code = source('tray/SettingsWindow.xaml.cs');

  assert.match(xaml, /<NavigationView\b/);
  assert.match(xaml, /PaneDisplayMode="Top"/);
  assert.match(xaml, /x:Key="SettingsPageHeaderStyle"/);
  assert.match(xaml, /x:Key="SettingsRowStyle"/);
  assert.match(code, /WindowSizing\.ResizeInDips\(this,\s*700,\s*590\)/);
  for (const tag of ['general', 'privacy', 'remote']) {
    assert.match(xaml, new RegExp(`Tag="${tag}"`));
  }
  for (const control of [
    'PresenceToggle',
    'StartupToggle',
    'UpdatesToggle',
    'LanguageSelect',
    'PresetSelect',
    'TaskTitleToggle',
    'ProjectToggle',
    'FileToggle',
    'TimerToggle',
    'RemoteList',
    'SaveButton',
  ]) {
    assert.match(xaml, new RegExp(`x:Name="${control}"`));
  }
  assert.match(code, /store\.Save\(config\)/);
  assert.match(code, /store\.StartsWithWindows/);
  assert.doesNotMatch(xaml, /DataGridView/);
});

test('Doctor exposes loading, results, rerun, and copy states', () => {
  const xaml = source('tray/DiagnosticsWindow.xaml');
  const code = source('tray/DiagnosticsWindow.xaml.cs');

  assert.match(xaml, /x:Name="RunningProgress"/);
  assert.match(xaml, /x:Name="ResultsList"/);
  assert.match(xaml, /x:Name="RunAgainButton"/);
  assert.match(xaml, /x:Name="CopyReportButton"/);
  assert.match(code, /diagnostics\.RunAsync/);
  assert.match(code, /Clipboard\.SetContent/);
  assert.match(code, /item\.Passed\s*\?\s*"PASS"\s*:\s*"FAIL"/);
});

test('notification-area lifecycle is native Win32, not WinForms', () => {
  const tray = source('tray/TrayIcon.cs');
  const coordinator = source('tray/AppCoordinator.cs');
  const lifecycle = `${tray}\n${coordinator}`;

  assert.match(tray, /Shell_NotifyIcon/);
  assert.match(tray, /TrackPopupMenu/);
  assert.match(coordinator, /ForegroundPollMs\s*=\s*2000/);
  assert.match(coordinator, /BackgroundPollMs\s*=\s*8000/);
  assert.match(lifecycle, /Pause presence/);
  assert.match(lifecycle, /Check for updates/);
  assert.doesNotMatch(tray, /System\.Windows\.Forms|new\s+NotifyIcon/);
});

test('WinUI smoke mode constructs every window before installer validation', () => {
  const app = source('tray/App.xaml.cs');
  const smoke = source('tests/installer-smoke.ps1');

  assert.match(app, /--ui-smoke/);
  assert.match(app, /new MainWindow/);
  assert.match(app, /new SettingsWindow/);
  assert.match(app, /new DiagnosticsWindow/);
  assert.match(app, /RunUiSmoke/);
  assert.match(app, /codex-presence-ui-smoke\.log/);
  assert.match(app, /WriteUiSmokeCheckpoint/);
  assert.match(smoke, /--ui-smoke/);
  assert.match(smoke, /codex-presence-ui-smoke\.log/);
  assert.match(smoke, /Get-Content[^\n]+\$uiSmokeLog/);
  assert.match(smoke, /WaitForExit\(30000\)/);
  assert.match(smoke, /timed out after 30 seconds/);
});

test('installer smoke exercises single-instance activation and keeps one tray host', () => {
  const smoke = source('tests/installer-smoke.ps1');

  assert.match(smoke, /\$activationProbe/);
  assert.match(smoke, /Activation probe returned/);
  assert.match(smoke, /single tray host/i);
  assert.match(smoke, /--discord-bridge/);
  assert.match(smoke, /single Social SDK bridge/i);
});

test('release smoke validates the portable WinUI bundle too', () => {
  const smoke = source('tests/installer-smoke.ps1');

  assert.match(smoke, /CodexPresence-\*-portable\.zip/);
  assert.match(smoke, /discord_partner_sdk\.dll/);
  assert.match(smoke, /DISCORD_SOCIAL_SDK_NOTICES\.txt/);
  assert.match(smoke, /Invoke-UiSmoke[^\n]+\$portableRoot/);
  assert.match(smoke, /-Label 'Portable UI smoke test'/);
  assert.match(smoke, /throw "\$Label \$failure/);
});
