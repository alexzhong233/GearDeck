#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#ifndef MyAppTag
  #define MyAppTag "v" + MyAppVersion
#endif
#ifndef Platform
  #define Platform "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\releases\v" + MyAppVersion + "\_stage_full"
#endif
#ifndef OutputDir
  #define OutputDir "..\releases\v" + MyAppVersion
#endif

[Setup]
AppId={{9A3E4C1B-5F0D-4B2C-8C19-4F0F3E8E22B1}
AppName=GearDeck
AppVersion={#MyAppVersion}
AppVerName=GearDeck {#MyAppVersion}
AppPublisher=GearDeck
AppPublisherURL=https://github.com/alexzhong233/GearDeck
AppSupportURL=https://github.com/alexzhong233/GearDeck/issues
DefaultDirName={localappdata}\Programs\GearDeck
DefaultGroupName=GearDeck
AllowNoIcons=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=GearDeck-Setup-{#MyAppTag}-win-{#Platform}
SetupIconFile=..\src\GearDeck.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\GearDeck.Desktop.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut for GearDeck"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GearDeck"; Filename: "{app}\GearDeck.Desktop.exe"
Name: "{group}\Uninstall GearDeck"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GearDeck"; Filename: "{app}\GearDeck.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GearDeck.Desktop.exe"; Description: "Launch GearDeck now"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopRunningGearDeck();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM GearDeck.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM GearDeck.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM GearDeck.Widget.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  StopRunningGearDeck();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningGearDeck();
  Result := True;
end;
