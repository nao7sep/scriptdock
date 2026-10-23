; Inno Setup script — builds dist\<name>-<version>-setup.exe from the
; self-contained win-x64 publish in publish-win\. The product name, publisher and
; version come from Directory.Build.props, passed in by scripts/package.ps1 via /D.
; iscc is pre-installed on windows-latest.

#define MyAppExe "ScriptDock.exe"
#ifndef MyAppVersion
  #error MyAppVersion is not defined - pass it via  iscc /DMyAppVersion=x.y.z
#endif
#ifndef MyAppName
  #error MyAppName is not defined - pass it via  iscc /DMyAppName=<Product>
#endif
#ifndef MyAppPublisher
  #error MyAppPublisher is not defined - pass it via  iscc /DMyAppPublisher=<Company>
#endif

[Setup]
; This .iss lives in scripts/, but the win-x64 publish output and the dist/ output
; folder are at the repo root — so resolve all source/output paths one level up.
SourceDir=..
AppName={#MyAppName}
AppId={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
Uninstallable=yes
OutputDir=dist
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=src\ScriptDock\icon.ico
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "publish-win\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
; Inno cannot recover a non-elevated user token for every elevated setup path.
; All-users installs launch later through their scoped shell shortcuts.
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not IsAdminInstallMode
