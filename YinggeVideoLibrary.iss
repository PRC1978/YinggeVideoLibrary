#define MyAppVersion "1.7.3.2"
#ifndef PortableSource
  #define PortableSource "..\发布\映格视频库.exe"
#endif
#ifndef ReleaseOutputDir
  #define ReleaseOutputDir "..\安装包"
#endif
#ifndef PackageAppId
  #define PackageAppId "PRC1978.YinggeVideoLibrary"
#endif
#ifndef PackageDefaultDir
  #define PackageDefaultDir "{localappdata}\Programs\YinggeVideoLibrary"
#endif
#ifndef PackageGroupName
  #define PackageGroupName "映格视频库"
#endif
#ifndef PostgresSource
  #define PostgresSource "artifacts\postgres-runtime"
#endif

[Setup]
AppId={#PackageAppId}
AppName=映格视频库
AppVersion={#MyAppVersion}
AppPublisher=PRC1978
AppPublisherURL=https://github.com/PRC1978/YinggeVideoLibrary
AppSupportURL=https://github.com/PRC1978/YinggeVideoLibrary/issues
AppUpdatesURL=https://github.com/PRC1978/YinggeVideoLibrary/releases
LicenseFile=LICENSE
DefaultDirName={#PackageDefaultDir}
DefaultGroupName={#PackageGroupName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseOutputDir}
OutputBaseFilename=YinggeVideoLibrary-Setup
; 安装包图标：v3 线场播放标志；回退时改回 app.ico（仓库根目录保留）。
SetupIconFile=assets\branding\yingge-icon-v3.ico
UninstallDisplayIcon={app}\映格视频库.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.7.3.2

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PortableSource}"; DestDir: "{app}"; Flags: ignoreversion
#ifndef BundlePostgres
  #define BundlePostgres 1
#endif
#if BundlePostgres
; The private database runtime is bundled for offline library conversion.
; Prepare the pinned EDB archive with scripts/prepare-postgres-runtime.ps1 first.
; Database files must remain in the user's data directory, never under {app}.
Source: "{#PostgresSource}\*"; DestDir: "{app}\postgres"; Flags: recursesubdirs createallsubdirs ignoreversion
#endif
Source: "assets\branding\yingge-icon-v3.ico"; DestDir: "{app}"; DestName: "yingge-icon-v3.ico"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "third-party\Microsoft.Web.WebView2\1.0.3595.46\LICENSE.txt"; DestDir: "{app}\third-party\Microsoft.Web.WebView2\1.0.3595.46"; Flags: ignoreversion
Source: "third-party\Microsoft.Web.WebView2\1.0.3595.46\NOTICE.txt"; DestDir: "{app}\third-party\Microsoft.Web.WebView2\1.0.3595.46"; Flags: ignoreversion
Source: "third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17\LICENSE.TXT"; DestDir: "{app}\third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion
Source: "third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17\THIRD-PARTY-NOTICES.TXT"; DestDir: "{app}\third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion
Source: "third-party\Microsoft.WindowsDesktop.App.Runtime.win-x64\9.0.17\LICENSE"; DestDir: "{app}\third-party\Microsoft.WindowsDesktop.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion

[Icons]
Name: "{group}\{#PackageGroupName}"; Filename: "{app}\映格视频库.exe"; IconFilename: "{app}\yingge-icon-v3.ico"
Name: "{autodesktop}\{#PackageGroupName}"; Filename: "{app}\映格视频库.exe"; IconFilename: "{app}\yingge-icon-v3.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\映格视频库.exe"; Description: "启动映格视频库"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The in-app updater writes "{app}\映格视频库.exe.update-previous" and may leave
; downloaded helpers in {app}\updates. Neither comes from [Files], so Inno Setup
; would keep them after uninstall. Remove only these two known leftovers.
Type: files; Name: "{app}\*.update-previous"
Type: filesandordirs; Name: "{app}\updates"

[Code]
var
  DeleteLibraryData: Boolean;
  LibraryDataDir: String;
  ConfirmedDataDir: String;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsUnsafeDataDirectory(const Path: String): Boolean;
var
  Candidate: String;
begin
  Candidate := Lowercase(AddBackslash(RemoveBackslashUnlessRoot(Path)));
  Result := (Candidate = Lowercase(AddBackslash(ExtractFileDrive(Path)))) or
    (Pos(Candidate, Lowercase(AddBackslash(GetEnv('USERPROFILE')))) = 1) or
    (Pos(Candidate, Lowercase(AddBackslash(ExpandConstant('{app}')))) = 1) or
    (Pos(Candidate, Lowercase(AddBackslash(GetWinDir))) = 1);
end;

function InitializeUninstall: Boolean;
var
  UserProfile: String;
  Choice: Integer;
  PathChoice: Integer;
  RequestedPath: String;
begin
  Result := True;
  DeleteLibraryData := False;
  if UninstallSilent then
    Exit;

  UserProfile := RemoveBackslashUnlessRoot(GetEnv('USERPROFILE'));
  if (UserProfile = '') or (ExtractFileDrive(UserProfile) = '') then
    Exit;
  LibraryDataDir := AddBackslash(UserProfile) + '.YinggeVideoLibrary';

  Choice := MsgBox('卸载时是否保留本地视频库数据？' + #13#10 + #13#10 +
    '是：保留资料、PostgreSQL 数据库、海报缓存和备份（推荐）。' + #13#10 +
    '否：卸载程序后删除以下默认数据目录的全部内容：' + #13#10 +
    LibraryDataDir + #13#10 + #13#10 +
    '如使用自定义数据目录，下一步可选择该目录。' + #13#10 +
    '视频原文件不在资料目录内时不受影响。' + #13#10 +
    '取消：停止卸载。', mbConfirmation, MB_YESNOCANCEL);
  if Choice = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;
  if Choice <> IDNO then
    Exit;

  PathChoice := MsgBox('要删除哪个视频库数据目录？' + #13#10 +
    '是：默认目录 ' + LibraryDataDir + #13#10 +
    '否：输入自定义数据目录' + #13#10 +
    '取消：保留数据，继续卸载程序。', mbConfirmation, MB_YESNOCANCEL);
  if PathChoice = IDCANCEL then
    Exit;
  if PathChoice = IDNO then
  begin
    RequestedPath := GetEnv('YINGGE_DATA_DIR');
    if not BrowseForFolder('选择软件使用的自定义视频库数据目录：',
      RequestedPath, False) then
      Exit;
    RequestedPath := Trim(RequestedPath);
    if (RequestedPath = '') or (ExtractFileDrive(RequestedPath) = '') then
    begin
      MsgBox('数据目录必须是完整路径。已保留数据。', mbError, MB_OK);
      Exit;
    end;
    LibraryDataDir := RemoveBackslashUnlessRoot(ExpandFileName(RequestedPath));
  end;

  if not DirExists(LibraryDataDir) then
  begin
    MsgBox('数据目录不存在，已保留数据：' + #13#10 + LibraryDataDir,
      mbInformation, MB_OK);
    Exit;
  end;
  if IsUnsafeDataDirectory(LibraryDataDir) then
  begin
    MsgBox('选择的是磁盘根目录或系统、用户、程序目录，已拒绝删除并保留数据：' + #13#10 +
      LibraryDataDir, mbError, MB_OK);
    Exit;
  end;
  if not (FileExists(AddBackslash(LibraryDataDir) + 'config.json') or
    FileExists(AddBackslash(LibraryDataDir) + 'library-index.json') or
    FileExists(AddBackslash(LibraryDataDir) + 'storage-postgresql.active')) then
  begin
    MsgBox('此目录缺少映格视频库数据标记，已保留数据：' + #13#10 + LibraryDataDir,
      mbError, MB_OK);
    Exit;
  end;

  if MsgBox('确定永久删除本地视频库数据？此操作无法撤销。' + #13#10 +
    LibraryDataDir + #13#10 + #13#10 +
    '请确认已备份所需资料。', mbCriticalError, MB_YESNO) = IDYES then
  begin
    ConfirmedDataDir := LibraryDataDir;
    DeleteLibraryData := True;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Attrs: Cardinal;
begin
  if (CurUninstallStep <> usPostUninstall) or (not DeleteLibraryData) then
    Exit;
  if (ConfirmedDataDir = '') or (LibraryDataDir <> ConfirmedDataDir) then
    Exit;
  if IsUnsafeDataDirectory(LibraryDataDir) then
    Exit;
  if not DirExists(LibraryDataDir) then
    Exit;
  Attrs := GetFileAttributesW(LibraryDataDir);
  if (Attrs = $FFFFFFFF) or ((Attrs and $400) <> 0) then
  begin
    MsgBox('数据目录是链接或无法确认，已保留数据：' + #13#10 + LibraryDataDir,
      mbError, MB_OK);
    Exit;
  end;
  if not DelTree(LibraryDataDir, True, True, True) then
    MsgBox('部分数据未能删除，请手动检查：' + #13#10 + LibraryDataDir,
      mbError, MB_OK);
end;


