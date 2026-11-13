; Instalador de Monitor de IPs (Inno Setup 6)
; Se compila desde release.ps1:  ISCC.exe /DAppVersion=1.3.0 /DSourceExe=... /DOutputDir=... installer\MonitorIPs.iss

#ifndef AppVersion
  #error Falta /DAppVersion=x.y.z (usa release.ps1)
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\MonitorIPs.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\release"
#endif

#define AppName      "Monitor de IPs"
#define AppExe       "MonitorIPs.exe"
#define AppPublisher "hujeanpaes"
#define AppURL       "https://github.com/hujeanpaes/monitor-de-ips"

[Setup]
; AppId fijo: las versiones nuevas actualizan la instalada. No cambiar.
AppId={{06804884-BF2D-4E09-A74C-09C5245E224C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador de {#AppName}

; Por usuario y sin administrador; se ofrece instalar para todos los usuarios.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; El monitor usa este mutex de instancia única (src/Program.cs): pide cerrarlo antes de actualizar/desinstalar.
AppMutex=Local\MonitorIPs_SingleInstance_v1
CloseApplications=yes

OutputDir={#OutputDir}
OutputBaseFilename=MonitorIPs-{#AppVersion}-Setup
LicenseFile=..\LICENSE
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
es.FinishedLabel=Se ha instalado [name] en el equipo.%n%nTus IPs, logs y exportes se guardan en la carpeta AppData\Roaming\MonitorIPs de tu usuario y se conservan al actualizar o desinstalar.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart";   Description: "Iniciar {#AppName} con Windows (en la bandeja, junto al reloj)"; GroupDescription: "Inicio:"; Flags: unchecked

[Registry]
; Mismo valor que usa el ajuste "Iniciar con Windows" de la aplicación (src/Tray.cs, clase Autostart).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MonitorIPs"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart
; Al desinstalar se quita siempre, aunque se haya activado desde la aplicación.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MonitorIPs"; Flags: uninsdeletevalue

[Files]
Source: "{#SourceExe}";       DestDir: "{app}"; Flags: ignoreversion
Source: "installed.mode";     DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";       DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md";    DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";         DestDir: "{app}"; DestName: "LICENCIA.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";               Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}";   Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";         Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
