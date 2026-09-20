# ScriptDock's areas, and the tests that stand for them

`dotnet test` runs this whole test project: at a few seconds it is already a fixed, balanced run, so
nothing selects a subset of it. ScriptDock has nothing paid, external, or heavy in the product, so
there is no separate full gate: `dotnet test` is it.

This file is the balance judgement the `tests-folder-conventions` require — which areas ScriptDock
has, and which tests stand for each — so a reader can tell what a green run covered, and an area with
no test standing for it is visible rather than merely absent. `ScriptDock.Tests/AreaMapTests.cs`
holds every path below to what is on disk.

Paths are relative to this folder. `Fakes/`, `PlatformFacts.cs`, `TestApp.cs`, `WindowTest.cs`,
`I18n/English.cs` and `Storage/StorageRootEnvironment.cs` are infrastructure, not tests, so no area
stands on them.

| Area | What it covers | Tests standing for it |
|---|---|---|
| Finding scripts | Scanning the configured roots, deciding what counts, and what changed since last time | `ScriptDock.Tests/Services/ScriptScannerTests.cs`, `ScriptDock.Tests/Services/IgnoreRulesTests.cs`, `ScriptDock.Tests/Services/ScriptListBuilderTests.cs`, `ScriptDock.Tests/Models/ScanDiffTests.cs`, `ScriptDock.Tests/Services/PathIdentityTests.cs`, `ScriptDock.Tests/Models/ScriptLabelsTests.cs` |
| Running a script | Starting it through a login shell, owning the process, and tree-killing it on restart | `ScriptDock.Tests/Services/ScriptProcessTests.cs`, `ScriptDock.Tests/Services/ProcessRunnerTests.cs`, `ScriptDock.Tests/Services/ShellCommandTests.cs`, `ScriptDock.Tests/Services/ExternalLauncherTests.cs` |
| The console | The run's output, the text typed back into a script that prompts, and stripped escapes | `ScriptDock.Tests/Views/ConsoleInputSubmissionTests.cs`, `ScriptDock.Tests/Views/BackgroundTextInputTests.cs`, `ScriptDock.Tests/Services/AnsiStripperTests.cs`, `ScriptDock.Tests/Controls/ComposingTextBoxTests.cs` |
| The Recent pane | Merging what is running with what was run, and acting on an entry | `ScriptDock.Tests/Services/RecentRunsTests.cs`, `ScriptDock.Tests/ViewModels/RecentListBuilderTests.cs`, `ScriptDock.Tests/Views/RecentActionAccessibilityTests.cs` |
| The main window's behaviour | What the window offers, what it guards, and what it asks before ending a run | `ScriptDock.Tests/ViewModels/MainWindowViewModelTests.cs`, `ScriptDock.Tests/ViewModels/MainWindowViewModelScriptsTests.cs`, `ScriptDock.Tests/Views/MainWindowCloseGuardTests.cs`, `ScriptDock.Tests/Views/ShortcutCatalogTests.cs` |
| Settings | The Settings dialog's draft, what it validates, and how it reports a rejected entry | `ScriptDock.Tests/ViewModels/SettingsDialogViewModelTests.cs`, `ScriptDock.Tests/Views/SettingsAccessibilityTests.cs`, `ScriptDock.Tests/Services/ConfigBootstrapTests.cs` |
| Storage | The app home, the JSON stores, their backups, and the shapes they round-trip | `ScriptDock.Tests/Storage/StorageRootTests.cs`, `ScriptDock.Tests/Storage/HomePathTests.cs`, `ScriptDock.Tests/Storage/JsonStoreTests.cs`, `ScriptDock.Tests/Storage/BackupStoreTests.cs`, `ScriptDock.Tests/Storage/AppStateRoundTripTests.cs`, `ScriptDock.Tests/Storage/ModelNormalizationTests.cs` |
| Logging | The session log, the run log, the scan report, and the secrets kept out of them | `ScriptDock.Tests/SessionLogTests.cs`, `ScriptDock.Tests/Services/SessionLoggerTests.cs`, `ScriptDock.Tests/Services/RunLogTests.cs`, `ScriptDock.Tests/Services/ScanReportLogTests.cs`, `ScriptDock.Tests/Services/LogRedactorTests.cs`, `ScriptDock.Tests/Services/LogRevealTests.cs` |
| Failure and notice | What the app says when something goes wrong, and where it says it | `ScriptDock.Tests/FailurePresentationTests.cs`, `ScriptDock.Tests/Views/AboutDialogTests.cs` |
| Interface language | The catalogues, the translator, and the guards that no key or hard-coded sentence reaches the screen | `ScriptDock.Tests/I18n/CatalogueTests.cs`, `ScriptDock.Tests/I18n/TranslatorTests.cs`, `ScriptDock.Tests/I18n/RenderedKeyTests.cs`, `ScriptDock.Tests/I18n/HardCodedTextTests.cs`, `ScriptDock.Tests/I18n/LanguageChangeTests.cs`, `ScriptDock.Tests/I18n/LabelFitTests.cs` |
| Window, theme, and menus | Window bounds and overflow, light and dark, the type, and the macOS menu bar | `ScriptDock.Tests/Views/WindowMetricsTests.cs`, `ScriptDock.Tests/Views/WindowOverflowTests.cs`, `ScriptDock.Tests/ThemeResourcesTests.cs`, `ScriptDock.Tests/AppStylesTests.cs`, `ScriptDock.Tests/UiFontTests.cs`, `ScriptDock.Tests/FontResolutionTests.cs`, `ScriptDock.Tests/Views/MacMenuBarTests.cs` |
| One instance at a time | The lease that keeps a second ScriptDock from taking over the first one's runs | `ScriptDock.Tests/Services/SingleInstanceLeaseTests.cs` |
| Identifiers and times | The ids the app mints and the timestamps it writes and shows | `ScriptDock.Tests/NanoIdTests.cs`, `ScriptDock.Tests/TimestampConventionsTests.cs`, `ScriptDock.Tests/Storage/UtcMillisDateTimeOffsetConverterTests.cs` |
| Packaging and the release surface | What ships, the installer, and the version the app claims | `ScriptDock.Tests/InstallerConfigurationTests.cs`, `ScriptDock.Tests/VersionConsistencyTests.cs` |
