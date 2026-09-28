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

test('app resources define one warm, accessible design system in both themes', () => {
  const app = source('tray/App.xaml');
  const project = source('tray/CodexPresence.Tray.csproj');

  assert.match(app, /<ResourceDictionary x:Key="Light">/);
  assert.doesNotMatch(app, /RequestedTheme="Dark"/, 'the app follows the appearance setting instead of forcing dark');
  assert.match(app, /x:Key="DisplayFontFamily">ms-appx:\/\/\/Fonts\/SourceSerif4Display-Regular\.ttf#Source Serif 4 Display, Georgia</);
  assert.match(project, /SourceSerif4Display-Regular\.ttf/);
  assert.ok(fs.existsSync(path.join(repository, 'assets', 'fonts', 'SourceSerif4-OFL.md')), 'the bundled serif ships with its license');
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
    'DisplayTitleTextStyle',
    'BodyTextStyle',
    'AccentSoftBrush',
    'ChipBorderStyle',
  ]) {
    assert.match(app, new RegExp(`x:Key="${token}"`));
  }
  assert.match(app, /TargetType="Button"/);
  assert.match(app, /MinHeight[^\n]*44/);
});

test('custom surfaces follow Windows High Contrast colors', () => {
  const app = source('tray/App.xaml');

  assert.match(app, /<ResourceDictionary x:Key="Dark">/);
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

test('the dashboard shows each agent with its own artwork', () => {
  const xaml = source('tray/MainWindow.xaml');
  const code = source('tray/MainWindow.xaml.cs');
  const project = source('tray/CodexPresence.Tray.csproj');
  const icon = path.join(repository, 'assets', 'codex-app-icon.png');

  assert.match(code, /WindowSizing\.ResizeInDips\(this,\s*680,\s*560\)/);
  assert.match(xaml, /codex-app-icon\.png/);
  assert.match(xaml, /claude-code-icon\.png/);
  assert.match(xaml, /x:Name="PreviewIconViewport"/);
  assert.match(xaml, /x:Name="PreviewClaudeIcon"/);
  assert.match(xaml, /Width="68"\s+Height="68"/, 'both artworks overscan the same 52 px viewport');
  for (const chip of ['CodexChip', 'ClaudeChip', 'AgentValue']) assert.match(xaml, new RegExp(`x:Name="${chip}"`));
  assert.match(code, /RenderChip\(ClaudeChip/);
  for (const asset of ['codex-app-icon.png', 'claude-code-icon.png', 'presence-mark.png']) assert.match(project, new RegExp(asset.replace('.', '\\.')));
  assert.ok(fs.statSync(icon).size > 20_000, 'the exact official artwork is bundled, not a placeholder glyph');
  assert.equal(
    crypto.createHash('sha256').update(fs.readFileSync(icon)).digest('hex'),
    '1c926e380bfe6a50f40648dd9bc5de88da7271546491adf99ec72172e17df6a0',
  );

  for (const window of ['MainWindow.xaml', 'SettingsWindow.xaml', 'DiagnosticsWindow.xaml']) {
    const titleBar = source(`tray/${window}`);
    assert.match(titleBar, /<TitleBar\.LeftHeader>\s*<Image\s+Width="18"\s+Height="18"\s+Margin="4,0,0,0"\s+Source="presence-mark\.png"/,
      `${window} shows the app's own mark, optically aligned away from the window edge`);
  }
});

test('Claude Code artwork shares the Codex icon grid and ships for Discord', () => {
  const png = (relativePath) => {
    const bytes = fs.readFileSync(path.join(repository, relativePath));
    assert.equal(bytes.toString('ascii', 1, 4), 'PNG');
    return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
  };
  assert.deepEqual(png('assets/claude-code-icon.png'), png('assets/codex-app-icon.png'), 'one overscan fits both agents');
  assert.deepEqual(png('assets/discord/claude-code.png'), { width: 1024, height: 1024 });
  assert.match(source('src/config.js'), /assets\/discord\/claude-code\.png/);
  assert.match(source('assets/brand/claude-spark.svg'), /identify Claude Code compatibility/);
});

test('Claude Code hooks are removed with the app and never block a turn', () => {
  const app = source('tray/App.xaml.cs');
  const installer = source('installer/CodexPresence.iss');
  const hooks = source('tray/ClaudeCodeHooks.cs');
  const hook = source('src/hook.js');

  assert.match(app, /--claude-hooks/);
  assert.match(installer, /--claude-hooks remove/);
  assert.match(hooks, /--agent claude/);
  assert.doesNotMatch(hooks, /"PreToolUse"/, 'no hook runs before every tool call');
  assert.match(hook, /agent === 'claude'[\s\S]+CLAUDE_REQUEST_TIMEOUT_MS[\s\S]+return;/);
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

test('control feedback retains native input and avoids competing brush transitions', () => {
  const motion = source('tray/Motion.cs');
  const app = source('tray/App.xaml');
  const settings = source('tray/SettingsWindow.xaml');
  const states = source('tray/InteractionStateManager.cs');
  assert.match(motion, /AnimationsEnabled/);
  assert.match(motion, /CreateCubicBezierEasingFunction/);
  assert.doesNotMatch(motion + states, /AttachButtonFeedback|PointerPressed\s*\+=|PointerEntered\s*\+=|PointerExited\s*\+=|KeyDown\s*\+=/);
  assert.match(states, /: VisualStateManager/);
  assert.match(states, /FocusState.Keyboard/);
  assert.match(states, /AccessibilitySettings/);
  assert.doesNotMatch(app, /BrushTransition/, 'button surfaces must not swap animated brushes');
  assert.match(app, /BasedOn="\{StaticResource DefaultButtonStyle\}"/);
  assert.doesNotMatch(source('tray/MainWindow.xaml.cs'), /Motion\.Reveal/, 'polling must not dim the preview');
  assert.match(settings, /TargetType="ToggleSwitch" BasedOn="\{StaticResource DefaultToggleSwitchStyle\}"/);
  assert.doesNotMatch(settings, /ToggleThumb.HorizontalAlignment|ToggleThumb.Margin/);
  assert.match(settings, /RadioButton GroupName="SettingsSections"/);
  assert.match(source('tray/MainWindow.xaml.cs'), /sessionTimer.Tick \+= \(_, _\) => RenderTime\(\)/);
});

test('WinUI windows size in logical pixels on high-DPI displays', () => {
  const sizing = source('tray/WindowSizing.cs');

  assert.match(sizing, /GetDpiForWindow/);
  assert.match(sizing, /DipsToPixels/);
  for (const windowCode of ['MainWindow.xaml.cs', 'SettingsWindow.xaml.cs', 'DiagnosticsWindow.xaml.cs']) {
    assert.match(source(`tray/${windowCode}`), /WindowSizing\.ResizeInDips\(this,/);
  }
});

test('settings use a compact Linear-style sidebar and preserve all configuration surfaces', () => {
  const xaml = source('tray/SettingsWindow.xaml');
  const code = source('tray/SettingsWindow.xaml.cs');

  const app = source('tray/App.xaml');
  assert.doesNotMatch(xaml, /<NavigationView\b/);
  assert.match(xaml, /x:Name="SettingsSidebar"/);
  assert.match(xaml, /x:Name="SettingsFooter"/);
  assert.match(xaml, /x:Key="SettingsNavButtonTemplate"/);
  assert.match(xaml, /x:Key="SettingsToggleStyle"/);
  assert.match(xaml, /x:Key="SettingsComboBoxItemStyle"/);
  assert.match(app, /x:Key="ComboBoxDropDownBackground"/, 'input brushes live in the theme dictionaries');
  assert.match(app, /x:Key="ComboBoxItemPillFillBrush"/);
  assert.doesNotMatch(xaml, /<StaticResource x:Key="ComboBox/, 'window-level aliases would freeze one theme');
  assert.doesNotMatch(xaml, /OnContent=|OffContent=/);
  assert.match(xaml, /x:Key="SettingsPageHeaderStyle"/);
  assert.match(xaml, /x:Key="SettingsRowContainerStyle"/);
  assert.match(code, /WindowSizing\.ResizeInDips\(this,\s*740,\s*620\)/);
  assert.match(code, /SectionButtonChecked/);
  assert.match(code, /SetActiveSection/);
  for (const tag of ['general', 'agents', 'privacy', 'remote']) {
    assert.match(xaml, new RegExp(`Tag="${tag}"`));
  }
  for (const navButton of ['GeneralNavButton', 'AgentsNavButton', 'PrivacyNavButton', 'RemoteNavButton']) {
    assert.match(xaml, new RegExp(`x:Name="${navButton}"`));
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
    'AppearanceSelect',
    'ClaudeToggle',
    'ClaudeActivityNameInput',
    'ClaudeHooksToggle',
    'ClaudeRemoteToggle',
    'ClaudeIdleSelect',
    'CodexToggle',
    'PreferredAgentSelect',
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
  assert.match(code, /"UNKNOWN"/);
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

test('secondary text and actions remain readable in both themes', () => {
  const app = source('tray/App.xaml');
  const luminance = (hex) => hex.match(/../g).map((part) => parseInt(part, 16) / 255)
    .map((value) => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4)
    .reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0);
  const contrast = (left, right) => {
    const [high, low] = [luminance(left), luminance(right)].sort((a, b) => b - a);
    return (high + 0.05) / (low + 0.05);
  };
  for (const theme of ['Light', 'Dark']) {
    const dictionary = app.split(`<ResourceDictionary x:Key="${theme}">`)[1].split('</ResourceDictionary>')[0];
    const color = (key) => dictionary.match(new RegExp(`x:Key="${key}" Color="#([A-Fa-f0-9]{6})"`))[1];
    for (const surface of ['WindowBackgroundBrush', 'CanvasBrush', 'SurfaceBrush', 'SurfaceRaisedBrush', 'SurfaceHoverBrush']) {
      const ratio = contrast(color('TextMutedBrush'), color(surface));
      assert.ok(ratio >= 4.5, `${theme}: secondary text contrast on ${surface}: ${ratio.toFixed(2)}:1`);
    }
    for (const state of ['Background', 'BackgroundPointerOver', 'BackgroundPressed']) {
      const ratio = contrast(color('PresenceAccentButtonForeground'), color(`PresenceAccentButton${state}`));
      assert.ok(ratio >= 4.5, `${theme}: primary button label contrast in ${state}: ${ratio.toFixed(2)}:1`);
    }
  }
});

test('presence actions cannot race and release their pending state after failures', () => {
  const code = source('tray/AppCoordinator.cs');
  assert.match(code, /if \(exiting \|\| presenceActionPending\) return/);
  assert.match(code, /finally\s*\{\s*presenceActionPending = false/);
  assert.match(source('tray/MainWindow.xaml.cs'), /presentation\.PauseEnabled && !presenceActionPending/);
  assert.doesNotMatch(source('tray/MainWindow.xaml'), /LIVE CARD/);
});

test('native switches, inputs and InfoBars use the app palette in each window theme', () => {
  const settings = source('tray/SettingsWindow.xaml');
  const dashboard = source('tray/MainWindow.xaml');
  for (const theme of ['Light', 'Dark']) {
    const scoped = settings.split(`<ResourceDictionary x:Key="${theme}">`)[1].split('</ResourceDictionary>')[0];
    assert.match(scoped, /x:Key="ToggleSwitchFillOn" Color="#(?:B55536|D97757)"/, `${theme} switches are clay, not the Windows accent`);
    assert.match(scoped, /x:Key="TextControlBorderBrushFocused"/);
    const info = dashboard.split(`<ResourceDictionary x:Key="${theme}">`)[1].split('</ResourceDictionary>')[0];
    assert.match(info, /x:Key="InfoBarWarningSeverityBackgroundBrush"/);
  }
});
