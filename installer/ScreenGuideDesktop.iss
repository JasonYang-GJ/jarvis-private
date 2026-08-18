#define MyAppName "元枢"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "元枢"
#define MyAppExeName "ScreenGuide.DesktopClient.exe"

[Setup]
AppId={{E2B9C242-2965-48BC-B2C6-CF83A2B11953}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Yuanshu
DefaultGroupName=元枢
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\release
OutputBaseFilename=元枢-V0.1.0-安装包
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Remove-ScreenGuideUserData.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\元枢"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--show"
Name: "{group}\删除元枢用户数据"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Remove-ScreenGuideUserData.ps1"""; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--show"; Description: "启动元枢"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeUninstall(): Boolean;
begin
  if not UninstallSilent then
    MsgBox('卸载只删除程序文件，不会删除本机任务历史和设置。若需删除用户数据，请先使用开始菜单中的“删除元枢用户数据”。', mbInformation, MB_OK);
  Result := True;
end;
