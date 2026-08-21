#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

[Setup]
AppId={{A6A1C0E0-8F0A-4E5E-9D1E-7E0F7D6A9B31}
AppName=Enfoque
AppVersion={#AppVersion}
AppVerName=Enfoque {#AppVersion}
AppPublisher=Jhonn Coronado
AppPublisherURL=https://github.com/Jhonncoronado/enfoque
AppSupportURL=https://github.com/Jhonncoronado/enfoque/issues
AppUpdatesURL=https://github.com/Jhonncoronado/enfoque/releases
SetupIconFile=..\Enfoque\vectorink.ico
DefaultDirName={autopf}\Enfoque
DefaultGroupName=Enfoque
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Enfoque-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayIcon={app}\Enfoque.exe
CloseApplications=yes
RestartApplications=yes
VersionInfoDescription=Instalador de Enfoque
VersionInfoProductName=Enfoque
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (c) 2026 Jhonn Coronado

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Enfoque"; Filename: "{app}\Enfoque.exe"; WorkingDir: "{app}"; Comment: "Oscurece la pantalla y enfoca áreas"

[Run]
Filename: "{app}\Enfoque.exe"; Description: "Iniciar Enfoque"; Flags: nowait postinstall skipifsilent
