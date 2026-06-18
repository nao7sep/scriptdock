# ScriptDock

ScriptDock is a local macOS desktop launcher for the `.command`/`.ps1` scripts scattered across your project repos. It scans the root directories you configure for scripts matching the extensions you choose, and presents them in a sorted Scripts pane of tiles alongside a Recent pane that merges what's currently running with what you ran recently — so you stop digging through Finder for the one you want. It runs a script as a child process it owns: double-click to run, double-click again to restart (it kills the whole process tree first, so dev servers free their ports cleanly), with each run's output kept in an in-app console you can read after it finishes and then dismiss. That last part is the point — instead of a coding session buried under a hundred Terminal tabs you can't tell apart, every run lives in one window you can clear when you're done.

It's for a developer who juggles many repos and restarts dev servers constantly, and who wants reliable restarts without the terminal clutter. Newly-found and vanished scripts are flagged after each scan; hidden items, recent runs, and window geometry persist between sessions; root directories, extensions, and regex ignore patterns are editable from a settings dialog. Scripts launch through a login shell so their `PATH` matches your terminal. ScriptDock is pre-release (0.x) and macOS-first; the Windows launchers exist but are less exercised.

<!-- TODO (dog-fooding): add a screenshot of the main window here. -->

## Requirements

- **macOS** (Apple Silicon or Intel).
- **.NET 10 SDK** to build and run — there is no packaged installer yet.
- The scripts ScriptDock launches run as **child processes it owns**, so quitting ScriptDock terminates anything still running. This is deliberate — it is what keeps restarts reliable and your terminals empty — but it means ScriptDock is for things you start and stop within a session, not long-lived background daemons.

## Getting started

Run `scripts/run-dev.command` (double-click in Finder, or run it from a shell) — the fastest way to try it. On first launch ScriptDock creates `~/.scriptdock/` and seeds it to scan `~/code` for `.command` scripts; edit the roots, extensions, and ignore patterns from the Settings dialog, then Rescan.

For the production-faithful build — a signed `ScriptDock.app` you can keep in your Dock — run `scripts/rebuild.command`.

## License

MIT © 2026 Yoshinao Inoguchi

## Contact

Yoshinao Inoguchi — nao7sep@gmail.com
