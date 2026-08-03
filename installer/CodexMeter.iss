#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\windows\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{B85A744E-C092-47CD-9E38-1B1FD2186961}
AppName=Codex Decision
AppVersion={#AppVersion}
AppPublisher=Codex Decision contributors
AppPublisherURL=https://github.com/mckrosekri/codex-meter
AppSupportURL=https://github.com/mckrosekri/codex-meter/issues
AppUpdatesURL=https://github.com/mckrosekri/codex-meter/releases
DefaultDirName={localappdata}\Programs\Codex Decision
DefaultGroupName=Codex Decision
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=CodexDecisionSetup-{#AppVersion}
SetupIconFile=..\windows\CodexMeterTray\Assets\CodexMeter.ico
UninstallDisplayIcon={app}\CodexMeterTray.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Codex Decision when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Codex Decision"; Filename: "{app}\CodexMeterTray.exe"; Parameters: "--show"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Codex Decision"; ValueData: """{app}\CodexMeterTray.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\CodexMeterTray.exe"; Parameters: "--show"; Description: "Launch Codex Decision"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\CodexMeterTray.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "StopCodexMeter"
