# ScriptDock

ScriptDock is a local macOS desktop launcher for the `.command`/`.ps1` scripts scattered across your project repos: instead of digging through Finder for the right one or losing a dev server in a wall of look-alike Terminal tabs, every run lives in one window you can clear when you're done.

It scans the root directories you configure and shows each matching script as a tile in a Scripts pane, beside a Recent pane that merges what's currently running with what you ran recently. Each script runs as a child process ScriptDock owns — double-click to run, double-click again to restart (it confirms, then tree-kills so dev servers free their ports, and relaunches) — with the run's output in an in-app console you can read, type into for scripts that prompt, and dismiss when done. Any action that ends a running script asks first. Scripts launch through a login shell, so their `PATH` matches your terminal.

It's for a developer who juggles many repos and restarts dev servers constantly. Newly-found and vanished scripts are flagged after each scan; hidden items, recent runs, and pane sizes persist between sessions; and root directories, extensions, and regex ignore patterns are editable from a Settings dialog. ScriptDock is pre-release (0.x) and macOS-first; the Windows launchers exist but are less exercised.

## Download

Prebuilt builds for **macOS (Apple Silicon)** and **Windows (x64)** are on the [Releases](https://github.com/nao7sep/scriptdock/releases) page — a `.dmg` / `setup.exe` installer or a portable `.zip`, whichever you prefer. These builds are **unsigned**, so the OS warns the first time you open one:

- **macOS** — right-click the app and choose **Open** (or run `xattr -dr com.apple.quarantine /Applications/ScriptDock.app`).
- **Windows** — on the SmartScreen prompt, click **More info → Run anyway**.

## Requirements

- **macOS** (Apple Silicon) or **Windows (x64)** to run a prebuilt download.
- **.NET 10 SDK** only if you build from source; the prebuilt downloads need nothing installed.
- The scripts ScriptDock launches run as **child processes it owns**. By default, quitting ScriptDock **leaves running scripts alive** and recaptures them on the next launch (matched by PID and start-time), so an accidental quit won't kill your in-progress work; you can configure it to terminate everything on quit instead (when that's on, quitting with scripts still running asks for confirmation first). Either way, a restart-while-running cleanly kills the whole process tree so dev servers free their ports.

## Run from source

Run `scripts/run-dev.command` (double-click in Finder, or run it from a shell) — the fastest way to try it. On first launch ScriptDock creates `~/.scriptdock/` and seeds sensible defaults. Add your project root directory (e.g. `~/code`) and adjust extensions and ignore patterns from the Settings dialog, then Rescan.

For the production-faithful build — an ad-hoc-signed `ScriptDock.app` you can keep in your Dock — run `scripts/rebuild.command`.

## License

MIT © 2026 Yoshinao Inoguchi

## Contact

Yoshinao Inoguchi — nao7sep@gmail.com
