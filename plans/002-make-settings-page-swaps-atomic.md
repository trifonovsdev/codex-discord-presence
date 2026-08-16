# 002 — Make Settings page swaps atomic

- **Status**: DONE
- **Commit**: 186c929
- **Severity**: MEDIUM
- **Category**: Purpose & frequency
- **Estimated scope**: 2 files, about 30 lines

## Problem

Settings hides and shows pages one control at a time while layout and painting
remain active. A blank or partially laid-out state can reach the screen between
those mutations.

```csharp
// tray/SettingsForm.cs:112 — current
var page = pages[selected];

foreach (Control candidate in pageHost.Controls) candidate.Visible = ReferenceEquals(candidate, page);
page.BringToFront();
page.Focus();
```

## Target

Batch the visibility and z-order changes inside `pageHost.SuspendLayout()` /
`ResumeLayout(false)`, then perform one layout and one invalidation. Keep the
swap immediate: settings tabs are a frequent navigation action, so adding a
200–500 ms modal-style transition would make the app feel slower. Preserve
focus unless a keyboard tab activation requires an explicit target.

## Repo conventions to follow

- Settings pages are registered once in `pages` before the first DPI autoscale pass.
- Selected state remains exposed through `ModernButton.IsSelected` and `AccessibleStates.Selected`.
- No animation dependency is used; custom motion runs through `MotionClock` only when motion explains a state change.

## Steps

1. In `tray/SettingsForm.cs`, batch page visibility, z-order, layout, and invalidation as one transaction.
2. Avoid unconditional `page.Focus()`, which can cause an AutoScroll container to jump.
3. Extend the regression test to require the atomic transaction and reject a settings page transition.
4. Extend Windows UI smoke to switch all tabs repeatedly and verify that no horizontal scrollbar becomes visible.

## Boundaries

- Do NOT animate the Settings tab contents.
- Do NOT rebuild pages on tab clicks.
- Do NOT change settings behavior or persisted values.
- Do NOT add dependencies.

## Verification

- **Mechanical**: `npm run check`; `dotnet build tray/CodexPresence.Tray.csproj -c Release`; installer `--ui-smoke` on Windows.
- **Feel check**: click General, Privacy, and SSH workspaces rapidly for 20 seconds. No blank frame, lateral shift, focus jump, or scrollbar flash may appear.
- **Done when**: every swap produces one settled page layout and repeated UI smoke passes with no horizontal overflow.
