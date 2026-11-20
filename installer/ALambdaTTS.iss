; ALambda TTS Inno Setup 脚本
; 打包：powershell -ExecutionPolicy Bypass -File build-installer.ps1
; 或者手动：ISCC.exe /DMyAppVersion=1.0.0 installer\ALambdaTTS.iss

#define MyAppName "ALambda TTS"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Anming_cat"
#define MyAppExeName "ALambdaTTS.exe"
#define MyAppId "{{6C7D9E45-2A18-4C31-9A77-2F3F0E5B7A11}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ALambda TTS
DefaultGroupName=ALambda TTS
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ALambdaTTS-Setup-{#MyAppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\install-dotnet.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\install-dotnet.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\install-dotnet.ps1"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall
Source: "..\assets\install-dotnet.cmd"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall

[Icons]
Name: "{group}\ALambda TTS"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 ALambda TTS"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ALambda TTS"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\install-dotnet.cmd"; Parameters: "/quiet"; StatusMsg: "正在检测并安装 .NET 10 桌面运行时…"; Flags: waituntilterminated; Check: NeedsDotNetRuntime
Filename: "{app}\{#MyAppExeName}"; Description: "启动 ALambda TTS"; Flags: nowait postinstall skipifsilent; Check: HasDesktopRuntime

[Code]
function HasRuntimeUnder(BaseDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(BaseDir + '\10.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime: Boolean;
begin
  Result :=
    HasRuntimeUnder(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasRuntimeUnder(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasRuntimeUnder(ExpandConstant('{localappdata}\Programs\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function NeedsDotNetRuntime: Boolean;
begin
  Result := not HasDesktopRuntime;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not HasDesktopRuntime) then
    MsgBox('没有检测到 .NET 10 桌面运行时，ALambda TTS 可能无法启动。' + #13#10 +
           '请运行安装目录里的 install-dotnet.cmd，或到 dotnet.microsoft.com 手动安装。',
           mbInformation, MB_OK);
end;
