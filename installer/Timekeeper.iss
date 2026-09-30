; Compile with scripts/build.ps1. Only the clean publish output and the new
; guides are packaged; historical reference material is never a source.
#ifndef AppVersion
  #define AppVersion "0.4.3"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{AECCDF49-3699-4D30-92EC-226715519C75}
AppName=Timekeeper
AppVersion={#AppVersion}
AppPublisher=RyanWW Products
AppPublisherURL=https://github.com/RyanWW-Products/Timekeeper
AppUpdatesURL=https://github.com/RyanWW-Products/Timekeeper/releases
DefaultDirName={localappdata}\Programs\Timekeeper
DefaultGroupName=Timekeeper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Timekeeper-Setup-{#AppVersion}-win-x64
UninstallDisplayIcon={app}\Timekeeper.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\Timekeeper"; Filename: "{app}\Timekeeper.exe"; WorkingDir: "{app}"
Name: "{group}\Timekeeper getting started"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\Help\GETTING_STARTED.md"""
Name: "{autodesktop}\Timekeeper"; Filename: "{app}\Timekeeper.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
; Retire the old agent-creation guides during an upgrade. Team members use a shared agent.
Type: files; Name: "{app}\Help\COPILOT_SETUP.md"
Type: files; Name: "{app}\Help\COPILOT_AGENT_INSTRUCTIONS.txt"

; User settings, exports and receipts are deliberately outside {app} and are
; retained by the standard uninstaller. No installer custom code runs APIs.
