; Inno Setup Skript für ServerManagerPro
; Aufruf: ISCC /DVersion=0.5.0 installer\ServerManagerPro.iss   (erwartet dist\ServerManagerPro.exe)
#ifndef Version
  #define Version "0.0.0"
#endif
#define AppName "ServerManagerPro"

[Setup]
AppId={{8F3C5B52-6D3A-4C57-9C86-2B1E6A0F7A11}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher=Florian Matscheko
AppPublisherURL=https://fmatsch.ist/ServerManagerPro/
AppSupportURL=https://github.com/fmatsch/ServerManagerPro/issues
AppUpdatesURL=https://github.com/fmatsch/ServerManagerPro/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\ServerManagerPro.exe
SetupIconFile=..\assets\icon.ico
OutputDir=..\dist
OutputBaseFilename=ServerManagerPro-Setup-{#Version}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 10 1607 / Windows Server 2016 und neuer, 64 Bit
MinVersion=10.0.14393
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; laufende Instanz (Overlay, Tray) beim Update schließen und danach wieder starten
CloseApplications=yes
RestartApplications=yes
VersionInfoVersion={#Version}
VersionInfoProductName={#AppName}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.AutostartTask=Overlay bei der Anmeldung im Hintergrund starten (für alle Benutzer)
english.AutostartTask=Start the overlay in the background at sign-in (all users)
german.OverlayIcon=ServerManagerPro Overlay
english.OverlayIcon=ServerManagerPro Overlay

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutostartTask}"; Flags: unchecked

[Files]
Source: "..\dist\ServerManagerPro.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ServerManagerPro.exe"
Name: "{group}\{cm:OverlayIcon}"; Filename: "{app}\ServerManagerPro.exe"; Parameters: "--overlay"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ServerManagerPro.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ServerManagerPro"; ValueData: """{app}\ServerManagerPro.exe"" --overlay"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ServerManagerPro.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
