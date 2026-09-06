# WinForms Shell With a Single WPF Glass Window

- Date: 2026-09-06
- Status: Implemented; remaining device-specific acceptance checks listed below.
- Supersedes: `../plan-a-rollback.md`.

## Decision

Keep the v1.1 application structure: a WinForms `ApplicationContext`, tray,
settings form, hotkey window, configuration and named-pipe IPC.
Only the editable glass surface uses WPF.

`Program` runs `Application.Run(AppHost)`. No WPF `Application` or second
message loop is created. `App.OpenGlass` calls
`ElementHost.EnableModelessKeyboardInterop` once for each new WPF window,
before showing it. IPC returns to the UI thread through a WinForms control.
Shutdown cancels the pipe listener, closes the glass windows and settings,
exits the WinForms loop and disposes the tray and hotkey window.

Existing uncommitted fixes to `GlassWindow.cs` were incorporated, not discarded.
The old `VK_PACKET` interception was removed: keyboard translation and text
composition are left to the framework, rather than extracting an 8-bit
character from a key message.

## Rendering

Each glass is one borderless, per-pixel-transparent WPF window:

- Window opacity stays at 1.
- Root background brush uses glass alpha.
- Text foreground and caret brush use text alpha.
- Both opacity settings accept integer percentages from 0 to 100, with
  synchronized sliders and numeric inputs. Zero is genuine zero alpha.
- The top drag strip and editor background are transparent.
- No color key, secondary background window, desktop capture or RGB blending
  is used to approximate text transparency.

For background color G, desktop D and text color C:

```text
B = glassAlpha * G + (1 - glassAlpha) * D
P = textAlpha * C + (1 - textAlpha) * B
```

The text brush alpha is independent of the background alpha. The final pixel
still naturally depends on the background when the text itself is translucent.
Glyph edge coverage is applied by the text renderer.

At zero background alpha, fully transparent areas pass mouse input through
according to native layered-window hit testing. The tray settings remain
available to restore visibility even when both settings are zero. No invisible
nonzero-alpha layer is added to intercept input.

## Compatibility and Input

- Configuration fields and settings controls are unchanged.
- Font size remains point-based in configuration, converted to WPF DIPs.
- Window bounds are saved/restored in physical screen pixels, as in v1.1.
  Bounds saved by the abandoned v1.2 WPF-only implementation used DIPs without
  a format marker. On scaled displays those particular bounds may need to be
  recentered/resized once.
- Middle-button handling uses preview events before the TextBox class handlers.
  Loss of capture, hiding, deactivation, mode changes and click-through stop
  scrolling; toggle mode never repeatedly steals capture back.
- Wheel handling is attached at the window, including the drag strip.
  Partial deltas accumulate, and Ctrl-wheel changes global zoom.
- Resize hit testing converts native screen coordinates with `PointFromScreen`.
- Dirty text is saved after the debounce delay, on hide/deactivation and before
  closing. Closing does not depend on a later application-wide save.

## Verification

Run on an interactive Windows desktop with the .NET 10 desktop SDK:

```powershell
dotnet run --project tests/GlassTXT.Tests.csproj
dotnet run --project tests/GlassTXT.Tests.csproj -- --interactive
```

The automated runner uses a unique pipe name, its own configuration/output
directory and disposable fixture documents. The main automated editor is
read-only to live desktop typing; test code still applies programmatic edits.
It does not register the normal global hotkey or change auto-start settings.

Automated checks cover:

- WinForms message-loop ownership with no WPF Application.
- v1.1 layout and font-size compatibility.
- Unicode save debounce, hide flush and external-change reload.
- Modeless WinForms settings.
- Wheel direction, partial deltas, zoom bounds and badge display.
- Hold/toggle/off middle scrolling and capture release.
- Click-through native styles.
- Eight resize hit-test results and position locking.
- Rendered pixel alpha when changing background or text opacity.
- Zero-alpha endpoints, typed numeric percentages, slider synchronization,
  invalid/out-of-range input, settings persistence and control bounds.
- IPC opening, duplicate-file reuse and multi-glass lifetime.
- Pending-edit save and resource cleanup on the last glass closing.

Pixel fixtures are written to `tests/bin/Debug/net10.0-windows/test-output`.
These exercise WPF rendering, not screenshots of the desktop compositor.

The interactive runner was also exercised through actual keyboard injection:
ASCII, Chinese Unicode text and a supplementary-plane character were entered,
read back from accessibility and confirmed in the saved file. Alt+F4 closed
the final glass and the process exited normally.

Still requires manual acceptance:

- Physical keyboard plus Chinese IME composition/candidate selection, including
  switching between the glass and settings while composing.
- Actual mouse dragging at all eight resize edges/corners and the top strip.
- Physical Ctrl-wheel and middle-button movement/release outside the window.
- Mixed-DPI multi-monitor dragging, negative monitor coordinates and display
  disconnect/reconnect.
- Explorer file drag-and-drop and multi-glass tray interactions.

The available desktop capture API returned
`SetIsBorderRequired failed (0x80004002)` on this machine. This prevented
screenshot-backed coordinate mouse automation; it is not a passed mouse test.

## Reference

Microsoft documents the modeless WinForms/WPF keyboard bridge:
https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.integration.elementhost.enablemodelesskeyboardinterop
