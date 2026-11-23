; 文件职责：将已验证的 Windows x64 自包含发布目录封装为当前用户的安装包。
; 安装范围：应用文件、开始菜单快捷方式和卸载记录。安装和卸载均不修改凭据、服务或会话。
; 构建入口：同目录 Build-Installer.ps1 提供版本、发布目录、输出目录和简体中文语言文件。

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
; 库的 XML 文档留给开发者，安装版不需要。不要排除运行时或依赖自带的其他 XML 文件。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\SideStage.Core.xml,\SideStage.Windows.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; 仅在同一目录升级首个试装版时逐个清理已更名的发行文件，不删除目录或用户数据。
Type: files; Name: "{app}\WinChildDesk.Core.dll"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.Core.xml"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.Windows.dll"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.Windows.xml"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.SessionAgent.exe"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.SessionAgent.dll"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.SessionAgent.deps.json"; Check: IsBetaOneUpgrade
Type: files; Name: "{app}\WinChildDesk.SessionAgent.runtimeconfig.json"; Check: IsBetaOneUpgrade

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

{ 直接复用原生向导的欢迎和确认页，不靠多一个弹窗来提示覆盖更新。 }
procedure CurPageChanged(CurPageID: Integer);
var
  Detail, Title: String;
begin
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
      WizardForm.PageNameLabel.Caption := Title;
      WizardForm.PageDescriptionLabel.Caption := FmtMessage('%1  →  %2', [InstalledVersion, PackageVersion]);
    end;
    wpFinished: begin
      WizardForm.FinishedHeadingLabel.Caption := FmtMessage(ModeMessage('UpgradeDone', 'RepairDone'), [PackageVersion]);
      if not WizardForm.YesRadio.Visible then
        WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('AfterMaintenance'), [PackageVersion]);
    end;
  end;
end;

{ 以打开现有文件的模式探测是否可替换，不创建、截断或写入字节。这样也能识别没有应用互斥量的早期版本。 }
function CheckReplaceable(Name: String): String;
var
  Stream: TFileStream;
begin
  Result := '';
  if not FileExists(AddBackslash(InstalledDir) + Name) then exit;
  try
    Stream := TFileStream.Create(AddBackslash(InstalledDir) + Name, fmOpenReadWrite or fmShareDenyNone);
    Stream.Free;
  except
    Result := FmtMessage(CustomMessage('FileBusy'), [Name]);
  end;
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

{ 仅对首个试装版原目录执行精确的旧组件清理，快照不受安装中途更新注册表的影响。 }
function IsBetaOneUpgrade: Boolean;
begin
  Result := (InstalledVersion = '0.1.0-beta.1') and (CompareText(RemoveBackslashUnlessRoot(InstalledDir),
    RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0);
end;
