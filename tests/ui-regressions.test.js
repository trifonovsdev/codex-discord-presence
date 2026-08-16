const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const repository = path.resolve(__dirname, '..');
const source = (relativePath) => fs.readFileSync(path.join(repository, relativePath), 'utf8');

test('desktop windows use the native Windows frame for drag, snap, and resize', () => {
  const components = source('tray/Components.cs');

  assert.match(
    components,
    /FormBorderStyle\s*=\s*resizable\s*\?\s*FormBorderStyle\.Sizable\s*:\s*FormBorderStyle\.FixedDialog/,
  );
  assert.match(components, /MinimumSize\s*=\s*SizeFromClientSize\(size\)/);
  assert.match(components, /Screen\.FromControl\(this\)\.WorkingArea/);
  assert.doesNotMatch(components, /WM_NCHITTEST|WM_NCCALCSIZE|class CaptionButton/);
});

test('interface icons are deterministic vector paths, not installed font glyphs', () => {
  const iconography = source('tray/Iconography.cs');

  assert.match(iconography, /icon\s+switch/);
  assert.match(iconography, /new Pen\(color,\s*strokeWidth\)/);
  assert.doesNotMatch(iconography, /Segoe Fluent Icons|Segoe MDL2 Assets|InstalledFontCollection|DrawString/);
  assert.doesNotMatch(iconography, /strokeWidth\s*\*\s*24f\s*\/\s*side/);
  for (const icon of ['Pause', 'Play', 'Settings', 'Diagnostics', 'Copy', 'Warning']) {
    assert.match(iconography, new RegExp(`UiIcon\\.${icon}\\s*=>`));
  }
  const brand = iconography.slice(iconography.indexOf('private static void DrawBrand'), iconography.indexOf('private static void DrawPause'));
  assert.doesNotMatch(brand, /DrawEllipse|FillEllipse/);
});

test('dashboard contains only activity, Discord preview, and essential controls', () => {
  const dashboard = source('tray/DashboardForm.cs');

  assert.doesNotMatch(dashboard, /SignalRelayControl|privacyRows|What Discord can see/);
  assert.match(dashboard, /SharingSummary/);
  assert.match(dashboard, /activityContext\.AutoEllipsis\s*=\s*true/);
  assert.match(dashboard, /alertAction/);
});

test('settings uses a compact horizontal tab bar without a duplicate Discord preview', () => {
  const settings = source('tray/SettingsForm.cs');

  assert.match(settings, /BuildTabBar/);
  assert.match(settings, /tabUnderline/);
  assert.match(settings, /tab\.Kind\s*=\s*ButtonKind\.Ghost/);
  assert.match(settings, /tab\.IsSelected\s*=\s*active/);
  assert.match(settings, /"Add",\s*ButtonKind\.Secondary/);
  assert.doesNotMatch(settings, /DiscordCardPreview|SettingsNavigationItem|navigationIndicator/);
});

test('settings toggle copy stays on one ellipsized line at narrow DPI layouts', () => {
  const components = source('tray/Components.cs');
  const toggleRow = components.slice(components.indexOf('public sealed class ToggleRow'), components.indexOf('public sealed class StatusPill'));

  assert.match(toggleRow, /description\.AutoSize\s*=\s*false/);
  assert.match(toggleRow, /description\.AutoEllipsis\s*=\s*true/);
  assert.match(toggleRow, /description\.SetBounds/);
  assert.match(components, /AccessibleStates\.Selected/);
  assert.match(components, /AccessibilityNotifyClients\(AccessibleEvents\.StateChange/);
});

test('all settings pages join the form before its first DPI autoscale pass', () => {
  const settings = source('tray/SettingsForm.cs');

  assert.match(settings, /RegisterPage\(tabs\[0\],\s*BuildGeneralPage\(\)\)/);
  assert.match(settings, /RegisterPage\(tabs\[1\],\s*BuildPrivacyPage\(\)\)/);
  assert.match(settings, /RegisterPage\(tabs\[2\],\s*BuildRemotePage\(\)\)/);
  assert.doesNotMatch(settings, /ShowPage\([^)]*,\s*Build[A-Za-z]+Page/);
});

test('the installed Windows app runs a hidden UI construction smoke test', () => {
  const program = source('tray/Program.cs');
  const smoke = source('tests/installer-smoke.ps1');

  assert.match(program, /--ui-smoke/);
  assert.match(program, /AccessibleRole\.PageTab/);
  assert.match(program, /PerformClick\(\)/);
  assert.match(smoke, /--ui-smoke/);
});
