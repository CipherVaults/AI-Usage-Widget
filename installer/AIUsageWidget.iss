#ifndef WidgetVersion
  #error Build the installer with Build.ps1 -Installer
#endif
#ifndef BuildDirectory
  #error Build the installer with Build.ps1 -Installer
#endif
#define WidgetAppId "{AB41514A-9B40-4BC5-B153-8223D45F4F01}"

[Setup]
AppId={{#WidgetAppId}
AppName=AI Usage Widget
AppVersion={#WidgetVersion}
DefaultDirName={code:InstallDirectory}
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
AllowUNCPath=no
AppMutex=AIUsageWidget.SingleInstance
MinVersion=10.0
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif
SetupIconFile=..\app\AIUsageWidget.ico
WizardSmallImageFile={#BuildDirectory}\WizardIcon.png
UninstallDisplayIcon={app}\AI Usage Widget v{#WidgetVersion}.ico
OutputDir={#BuildDirectory}
OutputBaseFilename=AI Usage Widget Setup v{#WidgetVersion}
VersionInfoVersion={#WidgetVersion}
VersionInfoDescription=AI Usage Widget Setup
VersionInfoProductName=AI Usage Widget
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#BuildDirectory}\AI Usage Widget.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\AIUsageWidget.ico"; DestDir: "{app}"; DestName: "AI Usage Widget v{#WidgetVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\AI Usage Widget"; Filename: "{app}\AI Usage Widget.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AI Usage Widget v{#WidgetVersion}.ico"; AppUserModelID: "AIUsageWidget.Desktop"
Name: "{userdesktop}\AI Usage Widget"; Filename: "{app}\AI Usage Widget.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AI Usage Widget v{#WidgetVersion}.ico"; AppUserModelID: "AIUsageWidget.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\AI Usage Widget.exe"; Description: "Launch AI Usage Widget"; Flags: nowait postinstall skipifsilent

[Code]
const
  WidgetUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#WidgetAppId}_is1';
  LegacyProductName = 'Quota Widget';
  FirstRenamedVersion = '1.0.0';
  InvalidFileAttrs = $FFFFFFFF;

function GetFileAttributes(lpFileName: String): DWORD;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsReparsePoint(const Path: String): Boolean;
var
  Attrs: DWORD;
begin
  Attrs := GetFileAttributes(Path);
  Result := (Attrs <> InvalidFileAttrs) and ((Attrs and FILE_ATTRIBUTE_REPARSE_POINT) <> 0);
end;

function IsSafeLocalInstallDir(const Dir: String): Boolean;
var
  Normalized, WinDir, SysDir: String;
  DriveLetter: Char;
begin
  Result := False;
  Normalized := RemoveBackslashUnlessRoot(Trim(Dir));
  if Length(Normalized) < 4 then
    Exit;
  if Copy(Normalized, 1, 2) = '\\' then
    Exit;
  DriveLetter := Uppercase(Normalized)[1];
  if (DriveLetter < 'A') or (DriveLetter > 'Z') or (Normalized[2] <> ':') or (Normalized[3] <> '\') then
    Exit;
  WinDir := RemoveBackslashUnlessRoot(ExpandConstant('{win}'));
  SysDir := RemoveBackslashUnlessRoot(ExpandConstant('{sys}'));
  if SameText(Normalized, WinDir) or SameText(Normalized, SysDir) or
     (Pos(Uppercase(AddBackslash(WinDir)), Uppercase(AddBackslash(Normalized))) = 1) then
    Exit;
  Result := True;
end;

function InstallDirectory(Param: String): String;
var
  PreviousDirectory, LegacyDefaultDirectory: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\AI Usage Widget');
  LegacyDefaultDirectory := ExpandConstant('{localappdata}\Programs\' + LegacyProductName);
  if RegQueryStringValue(HKCU, WidgetUninstallKey, 'InstallLocation', PreviousDirectory) and
     (Trim(PreviousDirectory) <> '') then
  begin
    PreviousDirectory := RemoveBackslashUnlessRoot(ExpandFileName(PreviousDirectory));
    if IsSafeLocalInstallDir(PreviousDirectory) and not IsReparsePoint(PreviousDirectory) and
       not SameText(PreviousDirectory, LegacyDefaultDirectory) then
      Result := PreviousDirectory;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion, UninstallCommand, InstallLocation, Uninstaller, FileName, OldExe: String;
  Previous, Current, RenamedVersion: Int64;
  Comparison, ExitCode, I: Integer;
begin
  Result := '';
  if not RegKeyExists(HKCU, WidgetUninstallKey) then
    Exit;

  if not RegQueryStringValue(HKCU, WidgetUninstallKey, 'DisplayVersion', OldVersion) or
     not StrToVersion(OldVersion, Previous) or not StrToVersion('{#WidgetVersion}', Current) then
  begin
    Result := 'Setup could not read the installed AI Usage Widget version. Uninstall it from Windows Settings and try again.';
    Exit;
  end;

  Comparison := ComparePackedVersion(Previous, Current);
  if Comparison = 0 then
    Exit;
  if Comparison > 0 then
  begin
    Result := 'A newer version of AI Usage Widget is already installed.';
    Exit;
  end;

  Result := 'Setup could not verify the previous AI Usage Widget uninstaller. Uninstall it from Windows Settings and try again.';
  if not RegQueryStringValue(HKCU, WidgetUninstallKey, 'UninstallString', UninstallCommand) or
     not RegQueryStringValue(HKCU, WidgetUninstallKey, 'InstallLocation', InstallLocation) or
     (Trim(InstallLocation) = '') then
    Exit;

  Uninstaller := RemoveQuotes(Trim(UninstallCommand));
  FileName := Lowercase(ExtractFileName(Uninstaller));
  InstallLocation := RemoveBackslashUnlessRoot(ExpandFileName(InstallLocation));
  if not IsSafeLocalInstallDir(InstallLocation) or IsReparsePoint(InstallLocation) or
     IsReparsePoint(Uninstaller) or
     (Length(FileName) <> 12) or (Copy(FileName, 1, 5) <> 'unins') or
     (ExtractFileExt(FileName) <> '.exe') or not FileExists(Uninstaller) or
     not SameText(ExtractFileDir(ExpandFileName(Uninstaller)), InstallLocation) then
    Exit;
  for I := 6 to 8 do
    if (FileName[I] < '0') or (FileName[I] > '9') then
      Exit;
  if StrToVersion(FirstRenamedVersion, RenamedVersion) and (ComparePackedVersion(Previous, RenamedVersion) < 0) then
    OldExe := AddBackslash(InstallLocation) + LegacyProductName + '.exe'
  else
    OldExe := AddBackslash(InstallLocation) + 'AI Usage Widget.exe';

  try
    ExtractTemporaryFile('AI Usage Widget.exe');
  except
    Result := 'Setup could not prepare the new version. Download the installer again and retry.';
    Exit;
  end;

  Log('Uninstalling the previous widget version ' + OldVersion);
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', InstallLocation,
    SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) or
    RegKeyExists(HKCU, WidgetUninstallKey) then
  begin
    Result := 'The previous installation could not be removed. Exit the previous widget from the tray and run Setup again.';
    Exit;
  end;
  if FileExists(OldExe) then
  begin
    NeedsRestart := True;
    Result := 'Restart Windows to finish removing the previous version, then run Setup again.';
    Exit;
  end;
  Result := '';
end;
