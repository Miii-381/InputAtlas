#ifndef MyAppVersion
  #define MyAppVersion "1.0.4"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\package"
#endif

#define MyAppName "InputAtlas"
#define MyAppExeName "InputAtlas.exe"

[Setup]
AppId={{8A64A192-AC87-4DE2-AE91-9EE26D20D73A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=InputAtlas
DefaultDirName={localappdata}\Programs\InputAtlas
DefaultGroupName=InputAtlas
OutputDir={#OutputDir}
OutputBaseFilename=InputAtlas-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no
WizardStyle=modern dynamic
DisableProgramGroupPage=yes
SetupLogging=yes
AppComments=自包含离线安装；无需预装或下载 .NET 10

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Data\*"

[Icons]
Name: "{group}\InputAtlas"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\InputAtlas"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 InputAtlas"; Flags: nowait postinstall skipifsilent

[Code]
const
  DriveFixed = 3;

var
  DeleteDataOnUninstall: Boolean;

function WindowsGetDriveType(RootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function IsForbiddenDirectory(const Directory: String): Boolean;
var
  Expanded: String;
begin
  Expanded := AddBackslash(ExpandFileName(Directory));
  Result :=
    (CompareText(Copy(Expanded, 1, Length(AddBackslash(ExpandConstant('{pf}')))), AddBackslash(ExpandConstant('{pf}'))) = 0) or
    (CompareText(Copy(Expanded, 1, Length(AddBackslash(ExpandConstant('{win}')))), AddBackslash(ExpandConstant('{win}'))) = 0) or
    (Pos('\\', Expanded) = 1) or
    (WindowsGetDriveType(AddBackslash(ExtractFileDrive(Expanded))) <> DriveFixed);
end;

function ProbeWritableDirectory(const Directory: String): Boolean;
var
  ProbePath: String;
  ProbeText: AnsiString;
begin
  Result := False;
  if not ForceDirectories(Directory) then Exit;
  ProbePath := AddBackslash(Directory) + '.inputatlas-write-probe';
  if not SaveStringToFile(ProbePath, 'InputAtlas', False) then Exit;
  if not LoadStringFromFile(ProbePath, ProbeText) then
  begin
    DeleteFile(ProbePath);
    Exit;
  end;
  DeleteFile(ProbePath);
  Result := ProbeText = 'InputAtlas';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if IsForbiddenDirectory(WizardDirValue) then
    begin
      MsgBox('请选择本地固定磁盘上的普通可写目录。Program Files、Windows、网络路径、映射盘和可移动磁盘不受支持。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not ProbeWritableDirectory(WizardDirValue) then
    begin
      MsgBox('所选目录未通过写入、读取和删除探测。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Log('InputAtlas 使用自包含 .NET 10 运行时，不执行运行时检测或在线下载。');
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Log('检测到已安装实例，正在请求安全退出以便升级。');
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--shutdown-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
    Log(Format('已安装实例退出请求完成，退出码：%d。', [ResultCode]));
  end;
end;

function InitializeUninstall: Boolean;
begin
  DeleteDataOnUninstall := MsgBox(
    '是否同时删除全部统计、设置、日志、备份和导出？' + #13#10 +
    '默认建议选择“否”以保留 Data。',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataPath: String;
begin
  if (CurUninstallStep = usUninstall) and DeleteDataOnUninstall then
  begin
    DataPath := AddBackslash(ExpandConstant('{app}')) + 'Data';
    if (Length(DataPath) > 10) and DirExists(DataPath) then
      DelTree(DataPath, True, True, True);
  end;
end;
