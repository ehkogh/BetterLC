#ifndef Version
  #define Version "0.0.0"
#endif

[Setup]
AppId={{6F1C2B9E-3D4A-4E7B-9C21-B37E5A1D8F40}
AppName=BetterLC
AppVersion={#Version}
AppVerName=BetterLC {#Version}
AppPublisher=BetterLC contributors
AppPublisherURL=https://github.com/ehkogh/BetterLC
AppSupportURL=https://github.com/ehkogh/BetterLC/issues
AppUpdatesURL=https://github.com/ehkogh/BetterLC/releases
DefaultDirName={autopf}\BetterLC
DefaultGroupName=BetterLC
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\Assets\betterlc.ico
UninstallDisplayIcon={app}\BetterLC.exe
UninstallDisplayName=BetterLC
LicenseFile=..\..\LICENSE
OutputDir=..\..\dist
OutputBaseFilename=BetterLC-windows-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#Version}
VersionInfoProductName=BetterLC

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\publish\BetterLC.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BetterLC"; Filename: "{app}\BetterLC.exe"
Name: "{autodesktop}\BetterLC"; Filename: "{app}\BetterLC.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BetterLC.exe"; Description: "{cm:LaunchProgram,BetterLC}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\BetterLC');
  if not DirExists(Data) or UninstallSilent then Exit;
  if MsgBox('Also delete BetterLC''s game files, Lunar settings and saved sign-in?' + #13#10 + Data,
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
