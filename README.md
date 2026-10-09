# ScriptDock

Find the start scripts across your projects, run them from one dashboard, and restart them cleanly with their ports freed. ScriptDock is a local desktop launcher for the `.command` and `.ps1` scripts scattered across your project repos on macOS and Windows: instead of digging through Finder or Explorer for the right one, or losing a dev server in a wall of look-alike terminal tabs, every run lives in one window you can clear when you're done.

It scans the root directories you configure and shows each matching script as a tile in a Scripts pane, beside a Recent pane that merges what's currently running with what you ran recently. Each script runs as a child process ScriptDock owns — double-click to run, double-click again to restart (it confirms, then tree-kills so dev servers free their ports, and relaunches) — with the run's output in an in-app console you can read, type into for scripts that prompt, and dismiss when done. Any action that ends a running script asks first. Scripts launch through a login shell, so their `PATH` matches your terminal.

It's for a developer who juggles many repos and restarts dev servers constantly. Newly-found and vanished scripts are flagged after each scan; hidden items, recent runs, and pane sizes persist between sessions; and root directories, extensions, and regex ignore patterns are editable from a Settings dialog. ScriptDock is macOS-first; the Windows launchers exist but are less exercised.

## Download

Prebuilt builds for **macOS (Apple Silicon)** and **Windows (x64)** are on the [Releases](https://github.com/nao7sep/scriptdock/releases/latest) page — a `.dmg` / `setup.exe` installer or a portable `.zip`, whichever you prefer. These builds are **unsigned**, so the OS warns the first time you open one:

- **macOS** — right-click the app and choose **Open** (or run `xattr -dr com.apple.quarantine /Applications/ScriptDock.app`).
- **Windows** — on the SmartScreen prompt, click **More info → Run anyway**.

## Requirements

- **macOS** (Apple Silicon) or **Windows (x64)** to run a prebuilt download.
- **PowerShell (`pwsh`) on PATH** on Windows, for every script type including `.bat` and `.cmd`.
- **.NET 10 SDK** only if you build from source; the prebuilt downloads include the .NET runtime.
- ScriptDock **owns the scripts it starts**, each as a child process. A run ends when its script exits; whatever a script hands to the system, such as an app opened with `open -n`, runs on its own. Quitting ScriptDock **stops every running script** after asking, and a restart-while-running kills the whole process tree, so dev servers free their ports.

ScriptDock supervises the shell process tree it launches while that tree remains attached. A script that deliberately daemonizes, double-forks, or otherwise escapes that tree is outside ScriptDock's supervision boundary; manage such a background service with its own service manager.

## Run from source

Run `scripts/run-dev.command` (double-click in Finder, or run it from a shell) — the fastest way to try it. On first launch it asks for the folder that holds your scripts (e.g. `~/code`) and scans it; add more root directories or adjust extensions and ignore patterns from the Settings dialog. ScriptDock uses built-in settings until you change them; its config file is created only when you save a changed setting.

For the production-faithful build — an ad-hoc-signed `ScriptDock.app` you can keep in your Dock — run `scripts/rebuild.command`.

## License

[GNU GPL v3 or later](LICENSE) © 2026 Yoshinao Inoguchi

## Contact

Yoshinao Inoguchi — yoshinao@inoguchi.com — <https://inoguchi.com>
