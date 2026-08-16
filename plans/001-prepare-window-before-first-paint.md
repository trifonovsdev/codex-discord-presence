# 001 — Prepare window geometry before first paint

- **Status**: DONE
- **Commit**: 186c929
- **Severity**: HIGH
- **Category**: Performance
- **Estimated scope**: 2 files, about 35 lines

## Problem

Every window is made visible before its bounds are clamped. The resize and move
therefore reach the screen as a second frame, which is perceived as a jump when
Settings opens.

```csharp
// tray/Components.cs:727 — current
protected override void OnShown(EventArgs e)
{
    base.OnShown(e);
    if (WindowState != FormWindowState.Normal) return;

    var workingArea = Screen.FromControl(this).WorkingArea;
    // ...
    Size = new Size(width, height);
    Location = new Point(/* clamped coordinates */);
}
```

## Target

Clamp bounds during `OnLoad`, before the first visible frame. Do not add a
manual opacity, size, or position animation. Let native DWM window motion remain
in charge. Modal windows must use `FormStartPosition.CenterParent` so their
initial and final positions are identical.

## Repo conventions to follow

- Native chrome is owned by `ModernForm` in `tray/Components.cs`.
- Dark native caption styling stays in `Visuals.ApplyWindowStyle(this)`.
- Settings remains a native modal dialog opened by `ShowDialog(owner)`.

## Steps

1. In `tray/Components.cs`, move working-area clamping from `OnShown` to a focused `PrepareInitialBounds` call in `OnLoad`.
2. Only assign `Size`, `MinimumSize`, or `Location` when the computed value differs, avoiding redundant layout passes.
3. In `tray/SettingsForm.cs`, set `StartPosition = FormStartPosition.CenterParent` before the dialog is shown.
4. Extend the UI regression and Windows smoke checks to prove geometry is prepared before `OnShown`.

## Boundaries

- Do NOT replace the native Windows frame.
- Do NOT animate top-level window opacity, position, width, or height.
- Do NOT add dependencies.
- If native `CenterParent` behavior cannot be retained with the current owner, stop instead of inventing custom hit testing.

## Verification

- **Mechanical**: `npm run check`; `dotnet build tray/CodexPresence.Tray.csproj -c Release`; `dotnet format tray/CodexPresence.Tray.csproj --verify-no-changes --no-restore`.
- **Feel check**: open Settings ten times from the dashboard at 100%, 150%, and 200% scaling. The dialog must appear at its final bounds on the first frame with no move or resize after becoming visible.
- **Done when**: `ModernForm` performs no geometry mutation in `OnShown`, Settings opens centered over its owner, and Windows UI smoke passes.
