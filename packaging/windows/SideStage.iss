; 文件职责：将已验证的 Windows x64 自包含发布目录封装为当前用户的安装包。
; 安装范围：应用文件、开始菜单快捷方式和卸载记录。安装和卸载均不修改凭据、服务或会话。
; 构建入口：同目录 Build-Installer.ps1 提供版本、发布目录、输出目录和简体中文语言文件。

#ifndef LegacyManifest
  #define LegacyManifest SourcePath + "\LegacyFlatFiles.sha256"
#endif

[Setup]
; 发布名称可以调整，但安装标识不可更换，否则 Windows 将把升级识别为第二份安装。
AppId={{28C18EA6-CBC7-4C56-AFC2-A5307EAD9573}
AppName=SideStage
AppVersion={#AppVersion}
AppVerName=SideStage {#AppVersion}
VersionInfoVersion={#FileVersion}
VersionInfoProductName=SideStage
VersionInfoDescription=SideStage Setup
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\SideStage
UsePreviousAppDir=yes
DisableWelcomePage=no
DisableReadyPage=no
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDirPath}
OutputBaseFilename=SideStage-{#AppVersion}-Setup-x64
SetupIconFile=..\..\assets\branding\SideStage.ico
UninstallDisplayIcon={app}\SideStage.exe
WizardStyle=modern
Compression=lzma2/normal
; 两个独立入口包含大量相同的框架代码；加大固实字典，使相邻的两个入口能共享压缩历史。
LZMADictionarySize=98304
SolidCompression=yes
; 更新遇到已占用的文件时，不自动关闭用户正在使用的远程会话窗口。
CloseApplications=no
RestartApplications=no

[Languages]
Name: "zhcn"; MessagesFile: "{#ChineseMessages}"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; 历史校验清单仅在升级时释放到安装器私有临时目录，不作为应用文件安装。
Source: "{#LegacyManifest}"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SideStage"; Filename: "{app}\SideStage.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\SideStage"; Filename: "{app}\SideStage.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\SideStage.exe"; Description: "{cm:LaunchProgram,SideStage}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
zhcn.UpgradeTitle=更新 SideStage
en.UpgradeTitle=Update SideStage
zhcn.RepairTitle=重新安装 SideStage
en.RepairTitle=Reinstall SideStage
zhcn.UpgradeDetail=已安装：%1%n更新至：%2
en.UpgradeDetail=Installed version: %1%nUpdate to: %2
zhcn.RepairDetail=此电脑已安装 SideStage %1。本次将重新安装同一版本。
en.RepairDetail=SideStage %1 is already installed. Setup will reinstall the same version.
zhcn.KeepData=安装目录：%1%n%n保留已保存的登录凭据和活动日志。如需保留正在使用的子会话，请先在 SideStage 中点「断开连接」，再关闭窗口。
en.KeepData=Installation folder: %1%n%nYour saved credentials and activity logs will be kept. To keep an active child session, disconnect in SideStage before closing its window.
zhcn.UpgradeReady=准备更新 SideStage
en.UpgradeReady=Ready to update SideStage
zhcn.InstallingUpdate=正在安装 SideStage 更新
en.InstallingUpdate=Installing the SideStage update
zhcn.RepairReady=准备重新安装 SideStage
en.RepairReady=Ready to reinstall SideStage
zhcn.UpgradeAction=更新(&U)
en.UpgradeAction=&Update
zhcn.RepairAction=重新安装(&R)
en.RepairAction=&Reinstall
zhcn.UpgradeConfirm=点击「更新」将 SideStage 从 %1 更新至 %2。
en.UpgradeConfirm=Click Update to update SideStage from %1 to %2.
zhcn.RepairConfirm=点击「重新安装」将替换 SideStage %1 的程序文件。
en.RepairConfirm=Click Reinstall to replace the program files for SideStage %1.
zhcn.UpgradeDone=SideStage 已更新至 %1。
en.UpgradeDone=SideStage has been updated to %1.
zhcn.RepairDone=SideStage %1 已重新安装。
en.RepairDone=SideStage %1 has been reinstalled.
zhcn.Downgrade=已安装 %1，此安装包是 %2。为避免覆盖较新版本，安装已停止。
en.Downgrade=Installed: %1. This installer contains %2. Setup stopped to avoid replacing a newer version.
zhcn.UnknownVersion=无法比较现有版本 %1 与安装包版本 %2。未修改现有安装。
en.UnknownVersion=Cannot compare installed version %1 with package version %2. The existing installation was not changed.
zhcn.OtherDirectory=更新或重新安装须使用原目录：%1
en.OtherDirectory=To update or reinstall, use the existing installation folder: %1
zhcn.FileBusy=无法替换 %1。请先退出 SideStage 再重试；需要保留子会话时，请先点「断开连接」。如果程序已退出，请检查安装目录的写入权限。
en.FileBusy=Cannot replace %1. Exit SideStage and retry; disconnect first to keep your child session. If the app is closed, check write access to the installation folder.
zhcn.AfterMaintenance=已安装版本：%1。登录凭据和活动日志保持不变。
en.AfterMaintenance=Installed version: %1. Your saved credentials and activity logs are unchanged.

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{28C18EA6-CBC7-4C56-AFC2-A5307EAD9573}_is1';
  PackageVersion = '{#AppVersion}';
var
  InstalledVersion, InstalledDir: String;
  IsRepair: Boolean;

{ 将发行版本拆为三段主版本、渠道顺序和渠道序号。正式版排在同一主版本的所有测试版之后。 }
function ReadVersion(Value: String; var Core: Int64; var Channel, Serial: Integer): Boolean;
var
  P: Integer;
  Suffix: String;
begin
  Result := False;
  P := Pos('-', Value);
  Channel := 3;
  Serial := 0;
  if P > 0 then begin
    Suffix := Copy(Value, P + 1, Length(Value));
    Delete(Value, P, Length(Value));
    P := Pos('.', Suffix);
    if P = 0 then exit;
    if Copy(Suffix, 1, P-1) = 'alpha' then Channel := 0
    else if Copy(Suffix, 1, P-1) = 'beta' then Channel := 1
    else if Copy(Suffix, 1, P-1) = 'rc' then Channel := 2
    else exit;
    Serial := StrToIntDef(Copy(Suffix, P+1, Length(Suffix)), -1);
    if Serial < 0 then exit;
  end;
  Result := StrToVersion(Value, Core);
end;

{ 在任何写入前读取一次卸载记录。首次安装、同版本重装、向前升级分别处理；旧包不降级。 }
function InitializeSetup: Boolean;
var
  OldCore, NewCore: Int64;
  OldChannel, NewChannel, OldSerial, NewSerial, Order: Integer;
  Reason: String;
begin
  Result := True;
  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', InstalledDir) or (InstalledDir = '') then begin
    { 应用内更新只允许覆盖现有安装，不能把已卸载的应用重新装回来。 }
    if ExpandConstant('{param:SIDESTAGEUPDATE|0}') = '1' then begin Result := False; exit; end;
    Log('SideStage mode: install');
    exit;
  end;
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', InstalledVersion) then InstalledVersion := '?';
  Reason := '';
  if ReadVersion(InstalledVersion, OldCore, OldChannel, OldSerial) and
     ReadVersion(PackageVersion, NewCore, NewChannel, NewSerial) then begin
    Order := ComparePackedVersion(OldCore, NewCore);
    if Order = 0 then Order := OldChannel - NewChannel;
    if Order = 0 then Order := OldSerial - NewSerial;
    IsRepair := Order = 0;
    if Order > 0 then Reason := 'Downgrade';
  end else Reason := 'UnknownVersion';
  Result := Reason = '';
  if not Result then begin
    Log('SideStage mode: blocked (' + Reason + ')');
    SuppressibleMsgBox(FmtMessage(CustomMessage(Reason), [InstalledVersion, PackageVersion]), mbError, MB_OK, IDOK);
  end else if IsRepair then Log('SideStage mode: reinstall')
  else Log('SideStage mode: update');
end;

{ 同一处选择更新或重装文案，确保窗口标题、确认按钮和完成页用词一致。 }
function ModeMessage(UpgradeKey, RepairKey: String): String;
begin
  if IsRepair then Result := CustomMessage(RepairKey) else Result := CustomMessage(UpgradeKey);
end;

{ 仅取消文件进度条自身的主题色，继续让 Inno 报告安装进度和处理辅助功能。 }
function SetWindowTheme(Wnd: HWND; AppName, Classes: String): Integer;
  external 'SetWindowTheme@uxtheme.dll stdcall';

{ COLORREF 按 BGR 排列；颜色对应应用内的 PaperFieldSurface.Brass 与 Hover。 }
procedure ApplyProgressColors;
var
  Gauge: HWND;
begin
  if HighContrastActive then exit;
  Gauge := WizardForm.ProgressGauge.Handle;
  { 显式传入指向零字符的字符串；空指针在此 API 中的含义是恢复系统主题。 }
  SetWindowTheme(Gauge, #0, #0);
  SendMessage(Gauge, $0409, 0, $003B5F7C); { PBM_SETBARCOLOR，黄铜 }
  SendMessage(Gauge, $2001, 0, $00C3DAE8); { PBM_SETBKCOLOR，浅纸色 }
end;

{ 直接复用原生向导的欢迎和确认页，不靠多一个弹窗来提示覆盖更新。 }
procedure CurPageChanged(CurPageID: Integer);
var
  Detail, Title: String;
begin
  if CurPageID = wpInstalling then ApplyProgressColors;
  if InstalledVersion = '' then exit;
  Title := ModeMessage('UpgradeTitle', 'RepairTitle');
  Detail := FmtMessage(ModeMessage('UpgradeDetail', 'RepairDetail'), [InstalledVersion, PackageVersion]);
  WizardForm.Caption := Title;
  case CurPageID of
    wpWelcome: begin
      WizardForm.WelcomeLabel1.Caption := Title;
      WizardForm.WelcomeLabel2.Caption := Detail + #13#10#13#10 +
        FmtMessage(CustomMessage('KeepData'), [RemoveBackslashUnlessRoot(InstalledDir)]);
    end;
    wpReady: begin
      WizardForm.PageNameLabel.Caption := ModeMessage('UpgradeReady', 'RepairReady');
      WizardForm.PageDescriptionLabel.Caption := Title;
      WizardForm.ReadyLabel.Caption := FmtMessage(ModeMessage('UpgradeConfirm', 'RepairConfirm'), [InstalledVersion, PackageVersion]);
      WizardForm.NextButton.Caption := ModeMessage('UpgradeAction', 'RepairAction');
    end;
    wpInstalling: begin
      if ExpandConstant('{param:SIDESTAGEUPDATE|0}') = '1' then
        WizardForm.PageNameLabel.Caption := CustomMessage('InstallingUpdate')
      else WizardForm.PageNameLabel.Caption := Title;
      WizardForm.PageDescriptionLabel.Caption := FmtMessage('%1  →  %2', [InstalledVersion, PackageVersion]);
    end;
    wpFinished: begin
      WizardForm.FinishedHeadingLabel.Caption := FmtMessage(ModeMessage('UpgradeDone', 'RepairDone'), [PackageVersion]);
      if not WizardForm.YesRadio.Visible then
        WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('AfterMaintenance'), [PackageVersion]);
    end;
  end;
end;

{ 以打开现有文件的模式探测是否可替换。代理刚退出时 Windows 仍可能短暂拒绝写入；
  最多等待三秒，期间只尝试打开文件。持续占用时将文件名交给向导提示用户。 }
function CheckReplaceable(Name: String): String;
var
  Stream: TFileStream;
  Attempt: Integer;
begin
  Result := '';
  if not FileExists(AddBackslash(InstalledDir) + Name) then exit;
  for Attempt := 0 to 12 do begin
    try
      Stream := TFileStream.Create(AddBackslash(InstalledDir) + Name, fmOpenReadWrite or fmShareDenyNone);
      Stream.Free;
      exit;
    except
      if Attempt < 12 then Sleep(250);
    end;
  end;
  Result := FmtMessage(CustomMessage('FileBusy'), [Name]);
end;

{ 写入前确认目录并检查主程序与会话代理；文件被占用时由用户退出或取消，不替用户结束窗口。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if InstalledDir = '' then exit;
  if CompareText(RemoveBackslashUnlessRoot(InstalledDir), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0 then
    Result := FmtMessage(CustomMessage('OtherDirectory'), [InstalledDir])
  else begin
    Result := CheckReplaceable('SideStage.exe');
    if Result = '' then Result := CheckReplaceable('SideStage.SessionAgent.exe');
    if (Result = '') and (InstalledVersion = '0.1.0-beta.1') then Result := CheckReplaceable('WinChildDesk.SessionAgent.exe');
  end;
end;

{ 读取 Windows 文件属性；不跟随用户在安装目录中放置的链接。 }
function GetFileAttributesW(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

{ 清单只含旧安装包顶层文件或一层语言目录的文件。先核对父目录、文件属性和摘要；
  不遍历用户目录，不删除不匹配的同名文件，且只在确实删过文件后尝试移除空语言目录。 }
procedure RemoveLegacyFile(const Root, Row: String);
var
  Parts: TArrayOfString;
  Path, Parent, Hash: String;
  I: Integer;
begin
  Parts := StringSplit(Row, ['|'], stAll);
  if GetArrayLength(Parts) < 2 then exit;
  Path := AddBackslash(Root) + Parts[0];
  Parent := ExtractFileDir(Path);
  if (Parent <> Root) and (ExtractFileDir(Parent) <> Root) then exit;
  if ((GetFileAttributesW(Parent) and $400) <> 0) or not FileExists(Path) or
     ((GetFileAttributesW(Path) and $400) <> 0) then exit;
  try
    Hash := GetSHA256OfFile(Path);
    for I := 1 to GetArrayLength(Parts) - 1 do
      if CompareText(Hash, Parts[I]) = 0 then begin
        if DeleteFile(Path) then begin
          if Parent <> Root then RemoveDir(Parent);
        end else Log('保留无法删除的旧文件：' + Path);
        exit;
      end;
    Log('保留内容已变化的旧文件：' + Path);
  except
    Log('保留无法校验的旧文件：' + Path);
  end;
end;

{ 等新文件成功写入才退役旧运行时，失败或取消的升级不损坏原版。只处理已登记的原安装目录。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Root: String;
  Rows: TArrayOfString;
  I: Integer;
begin
  if (CurStep <> ssPostInstall) or (InstalledDir = '') then exit;
  if Pos('|' + InstalledVersion + '|',
    '|0.1.0-beta.1|0.1.0-beta.2|0.1.0-beta.3|0.1.0-beta.4|0.1.0-beta.5|0.1.0-beta.6|') = 0 then exit;
  Root := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if (CompareText(RemoveBackslashUnlessRoot(InstalledDir), Root) <> 0) or
     ((GetFileAttributesW(Root) and $400) <> 0) then exit;
  ExtractTemporaryFile('LegacyFlatFiles.sha256');
  if LoadStringsFromFile(ExpandConstant('{tmp}\LegacyFlatFiles.sha256'), Rows) then
    for I := 0 to GetArrayLength(Rows) - 1 do RemoveLegacyFile(Root, Rows[I]);
end;
