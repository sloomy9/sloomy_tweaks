; Sloomy Tweaks installer (Inno Setup 6). Built by build.ps1 -BuildInstaller.
; Installs per-machine to Program Files; the app itself runs unelevated (asInvoker).

#ifndef SourceDir
  #define SourceDir "..\artifacts\SloomyTweaks-win-x64"
#endif
#define AppName "Sloomy Tweaks"
#define AppVersion "0.2.0"
#define AppExe "SloomyTweaks.exe"

[Setup]
AppId={{6E1B7F3A-5C2D-4B8E-9A41-2F7D3C9E1A55}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Sloomy Tweaks
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\Sloomy Tweaks
DefaultGroupName=Sloomy Tweaks
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=SloomyTweaks-Setup-{#AppVersion}
SetupIconFile=..\src\SloomyTweaks.App\Assets\SloomyTweaks.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Sloomy Tweaks"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Sloomy Tweaks"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
; runasoriginaluser: start the app unelevated even though setup ran as administrator.
Filename: "{app}\{#AppExe}"; Description: "Launch Sloomy Tweaks"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; User data (backups of original settings, history, logs) in %LOCALAPPDATA%\Sloomy Tweaks is kept on purpose,
; so settings can still be restored after a reinstall. Users can delete that folder manually.
