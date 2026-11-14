# Next Island interface

Next Island should stay quiet until it is useful. The desktop panel is an intentional interaction; the game HUD is a steady peripheral reading. Keep the current black silhouette and stretch-and-pinch reveal. Do not add looping decoration to the HUD.

## Shared system

`WindowsGameHud/IslandTheme.xaml` owns the palette, font, insets, rounding, and original vector icons. Use Segoe UI Variable Text with Segoe UI fallback. Primary text is near-white; secondary text stays legible against the near-black surface. The pale blue accent identifies selection and progress. Album artwork supplies the remaining color.

The desktop panel is 384 DIPs wide, shrinking to 328 DIPs on narrow displays. The navigation occupies the same top position in every desktop view. A shared selection surface slides between its four destinations. Media content, shelf files, clipboard entries, and timer state are the focus beneath it. Navigation is omitted from expanded game playback.

The compact pill remains 38 DIPs tall with a content-driven width of 176–280 DIPs. The game HUD remains 26 DIPs tall, or 32 with larger text. Its existing responsive widths, two-row fallback, click-through behavior, telemetry providers, and detection rules are preserved. The music pocket moves alongside telemetry and playback expands underneath it only on request.

## Interaction details

- Transport buttons use filled playback glyphs. Play/Pause has a distinct circular surface and a 44-DIP target. Other targets are at least 32 DIPs.
- Mouse presses compress the control. Keyboard focus remains visible. Long media titles truncate and expose their full text in a tooltip.
- Tab selection moves over 220 ms; incoming content settles over 170 ms. Retargeting uses the current visible values. The outer shape uses its existing interruptible, critically damped springs.
- Reduced motion removes displacement and stops playback bars. Hidden and paused bars do not keep a timer running.
- Shelf drag targets highlight while accepting a drop. Missing files, shelf limits, and open failures show actionable inline feedback. Enter opens a selected file; Delete removes its shelf entry while preserving the original file.
- Clipboard history remains opt-in and session-only. Copy reports success, and Ctrl+C, Enter, or double-click copies the selected entry. Clipboard failures are visible.
- Timer state distinguishes ready, counting down, paused, and finished. The progress line represents actual remaining time; Resume preserves that time. Reset is disabled when there is nothing to reset.

## Verification

Run `dotnet run --project Verification -c Release` on Windows with the .NET 10 SDK. The runner checks state transitions and creates WPF render captures under its output directory. It covers hidden transparency, deliberate hover, game click-through, media event classification, repeated and interrupted transitions, paused animation shutdown, display widths, long readings, fixed navigation, timer state, narrow error layouts, and action clipping after navigation.

Use `dotnet run --project Verification -c Release -- --interactive` for a four-minute control host with sample media. The normal test-window chrome belongs to the host; the embedded panel is the app's actual control. This is a test fixture, not a real media session. It does not validate live-game focus return, anti-cheat compatibility, FPS capture, or physical mixed-DPI displays. Those need separate Windows hardware testing.

The overview captures use illustrative artwork and readings. Inspect the rendered app at 100% scale as well as the overview. Build success and fixture checks are evidence, not a substitute for live-game testing.
