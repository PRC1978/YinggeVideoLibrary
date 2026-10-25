#define MyAppVersion "1.5.4"

[Setup]
AppId=PRC1978.YinggeVideoLibrary
AppName=映格视频库
AppVersion={#MyAppVersion}
AppPublisher=PRC1978
AppPublisherURL=https://github.com/PRC1978/YinggeVideoLibrary
AppSupportURL=https://github.com/PRC1978/YinggeVideoLibrary/issues
AppUpdatesURL=https://github.com/PRC1978/YinggeVideoLibrary/releases
LicenseFile=LICENSE
DefaultDirName={localappdata}\Programs\YinggeVideoLibrary
DefaultGroupName=映格视频库
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\安装包
OutputBaseFilename=YinggeVideoLibrary-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\映格视频库.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.5.4.0

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "..\发布\映格视频库.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "third-party\Microsoft.Web.WebView2\1.0.3595.46\LICENSE.txt"; DestDir: "{app}\third-party\Microsoft.Web.WebView2\1.0.3595.46"; Flags: ignoreversion
Source: "third-party\Microsoft.Web.WebView2\1.0.3595.46\NOTICE.txt"; DestDir: "{app}\third-party\Microsoft.Web.WebView2\1.0.3595.46"; Flags: ignoreversion
Source: "third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17\LICENSE.TXT"; DestDir: "{app}\third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion
Source: "third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17\THIRD-PARTY-NOTICES.TXT"; DestDir: "{app}\third-party\Microsoft.NETCore.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion
Source: "third-party\Microsoft.WindowsDesktop.App.Runtime.win-x64\9.0.17\LICENSE"; DestDir: "{app}\third-party\Microsoft.WindowsDesktop.App.Runtime.win-x64\9.0.17"; Flags: ignoreversion

[Icons]
Name: "{group}\映格视频库"; Filename: "{app}\映格视频库.exe"
Name: "{autodesktop}\映格视频库"; Filename: "{app}\映格视频库.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\映格视频库.exe"; Description: "启动映格视频库"; Flags: nowait postinstall skipifsilent
