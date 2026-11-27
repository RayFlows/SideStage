/*
 * 文件名称：ParchmentPreviewForm.Environment.cs
 * 文件职责：展示宿主权限、本机 Windows 版本、远程桌面和凭据状态，响应用户的子会话启用请求。
 * 采样方式：在窗口可见时检查服务和预览控件，在窗口激活或凭据变更时检查保存项。
 * 操作边界：环境检查只读，用户点击启用后才调用 Windows 子会话配置接口并复查开关。
 *   首页不试登录、不启动服务，诊断信息不含账户或密码。
 */

using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private long _nextRdpCheck;
    private bool _rdpPrepared;
    private bool _enablingChildSessions;
    private Label? _privilegeIcon, _privilegeText;
    private bool? _administrator;

    /// <summary>
    /// 将下次启动的 UAC 请求开关显示在高级设置中。写入失败时读回实际偏好，避免开关显示为已保存。
    /// </summary>
    private void AddStartupPermissionSetting(TableLayoutPanel options)
    {
        var list = new SettingsList();
        var request = new SettingsSwitch("启动时请求管理员权限", _bodyFont)
        {
            Checked = StartupElevation.IsEnabled(_updatePreferenceKey)
        };
        bool restoring = false;
        request.CheckedChanged += (_, _) =>
        {
            if (restoring) return;
            try { StartupElevation.SetEnabled(_updatePreferenceKey, request.Checked); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                restoring = true;
                try { request.Checked = StartupElevation.IsEnabled(_updatePreferenceKey); }
                finally { restoring = false; }
                ShowError("无法保存启动权限设置", ex);
            }
        };
        list.AddRow(request);
        options.Controls.Add(list);
        options.Controls.Add(SettingsCaption.Footnote(
            "建议开启，否则某些功能无法使用。", _labelFont));
    }

    /// <summary>
    /// 主窗口初始化后读取一次本次进程的权限。将读取失败单独显示，供用户区分权限未知和普通权限。
    /// </summary>
    private void RefreshPrivilegeCheck()
    {
        bool? administrator;
        try { administrator = StartupElevation.IsAdministrator(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or Win32Exception)
        {
            administrator = null;
        }
        DisplayPrivilegeCheck(administrator);
    }

    /// <summary>
    /// 首页和活动日志给出同一条权限提示。状态文字可由屏幕阅读器读取，悬停可查看恢复建议。
    /// </summary>
    internal void DisplayPrivilegeCheck(bool? administrator)
    {
        _administrator = administrator;
        ApplyRecoveryPrivilege(administrator == true);
        if (_startupPrivilegeWarning != null) _startupPrivilegeWarning.Visible = administrator == false;
        if (_privilegeText is null) return;
        var (level, status, detail) = administrator switch
        {
            true => (CheckResult.Ok, "管理员", "SideStage 正以管理员身份运行。会话恢复仍需本机控制台空闲。"),
            false => (CheckResult.Warn, "普通权限 · 主桌面恢复已停用",
                "可正常连接分身。如需启用主桌面恢复，请以管理员身份运行。"),
            null => (CheckResult.Warn, "权限未知", "无法确认本次运行的管理员权限，主桌面恢复已停用。"),
        };
        SetEnvironmentCheck(_privilegeIcon, _privilegeText, level, status, detail);
        _privilegeText.AccessibleDescription = detail;
        if (level == CheckResult.Warn) AppendLog($"{status}。{detail}", LogLevel.Warning);
    }

    /// <summary>
    /// 窗口首次显示后在后台查询 Windows 自报的本地化产品名。系统名称仅供识别，
    /// 查询失败时仍允许按实际能力连接，关闭窗口后不再回写控件。
    /// </summary>
    private async void ShowWindowsEdition()
    {
        if (_systemText is null) return;
        WindowsEdition? edition = null;
        try
        {
            edition = await Task.Run(WindowsEdition.Read);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                       or System.Security.SecurityException or System.TimeoutException)
        {
            if (!IsDisposed && !Disposing) AppendLog($"无法读取 Windows 版本：{ex.Message}", LogLevel.Warning);
        }
        if (!IsDisposed && !Disposing) DisplayWindowsEdition(edition);
    }

    /// <summary>把同一次 WMI 查询的系统名称和兼容性结果显示在一行，不参与连接及权限判断。</summary>
    internal void DisplayWindowsEdition(WindowsEdition? edition)
    {
        if (_systemText is null) return;
        _systemText.Text = edition?.Name ?? "Windows（版本未识别）";
        _editionIndicator?.Apply(edition?.Mark ?? EditionMark.Unknown);
    }

    /// <summary>区分开关关闭、已启用和无法查询三种状态。只有明确关闭时才提供启用操作。</summary>
    private void UpdateCapabilityCheck(bool? enabled)
    {
        if (_enableChildButton is null) return;
        _enableChildButton.Visible = enabled == false;
        _enableChildButton.Enabled = !_enablingChildSessions;
        UpdateCheckItem(_checkCapabilityIcon, _checkCapabilityText,
            enabled == true ? CheckResult.Ok : CheckResult.Error,
            enabled switch { true => "已启用", false => "未启用", null => "查询失败" });
    }

    /// <summary>
    /// 响应首页的启用请求，操作期间锁住按钮。仅在 Windows 回报开关已经打开后显示成功。
    /// 取消管理员授权可重新发起操作，其他错误交由日志和系统提示显示。
    /// </summary>
    private async void EnableChildSessions(object? sender, EventArgs e)
    {
        if (_enablingChildSessions || _enableChildButton?.Visible != true) return;
        _enablingChildSessions = true;
        _enableChildButton.Enabled = false;
        try
        {
            if (!_sessionInspector.IsChildSessionsEnabled()) await _enableChildSessions();
            if (IsDisposed || Disposing) return;
            if (!_sessionInspector.IsChildSessionsEnabled())
                throw new InvalidOperationException("Windows 已处理启用请求，但子会话开关仍然关闭。");
            AppendLog("Windows 子会话功能已启用。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!IsDisposed) AppendLog("已取消启用子会话。", LogLevel.Warning);
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            if (!IsDisposed) ShowError("无法启用子会话", ex);
        }
        finally
        {
            _enablingChildSessions = false;
            if (!IsDisposed && !Disposing) RefreshSessionStatus();
        }
    }

    /// <summary>
    /// 检查凭据管理器中当前用户的应用保存项，并立即更新首页。
    /// 未保存时保留连接入口，让用户在连接时填写；系统查询失败则单独报错。
    /// </summary>
    private void RefreshCredentialCheck()
    {
        if (_checkCredentialText is null || _configureCredentialsButton is null) return;
        try
        {
            bool saved = _credentialStore.HasSavedCredentials();
            _configureCredentialsButton.Visible = !saved;
            SetEnvironmentCheck(_checkCredentialIcon, _checkCredentialText,
                saved ? CheckResult.Ok : CheckResult.Pending,
                saved ? "已保存到本机" : "未保存 · 建议配置",
                saved ? "连接时将使用本机 Windows 凭据管理器中的保存项。"
                      : "保存后连接时会自动填写 Windows 账户密码，也可以在连接时手动填写。");
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            _configureCredentialsButton.Visible = false;
            SetEnvironmentCheck(_checkCredentialIcon, _checkCredentialText, CheckResult.Error,
                "读取失败", ex.Message);
        }
    }

    /// <summary>
    /// 检查预览实际使用的 ActiveX 和本机 TermService。仅在首页可见时每五秒复查，
    /// 服务查询仅提供诊断，保留 Windows 按需启动服务的机会。
    /// </summary>
    private void RefreshLocalRdpCheck()
    {
        if (_checkRdpText is null || _centerCard?.Visible != true) return;
        long now = Environment.TickCount64;
        if (now < _nextRdpCheck) return;
        _nextRdpCheck = now + 5000;

        try
        {
            // PreparePreview 在同一个原生控件上缓存成功配置；句柄重建后由控件重新准备。
            _previewPanel.PreviewControl.PreparePreview();
            _rdpPrepared = true;
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            _rdpPrepared = false;
            SetEnvironmentCheck(_checkRdpIcon, _checkRdpText, CheckResult.Error, "预览控件不可用",
                $"RDP 控件准备失败。{ex.GetBaseException().Message}");
            return;
        }

        try
        {
            using ServiceController service = new("TermService");
            var (level, text) = service.Status switch
            {
                ServiceControllerStatus.Running => (CheckResult.Ok, "服务和控件就绪"),
                ServiceControllerStatus.Stopped => (CheckResult.Warn, "服务已停止"),
                ServiceControllerStatus.Paused => (CheckResult.Warn, "服务已暂停"),
                ServiceControllerStatus.StartPending => (CheckResult.Pending, "服务正在启动"),
                ServiceControllerStatus.StopPending => (CheckResult.Pending, "服务正在停止"),
                ServiceControllerStatus.PausePending => (CheckResult.Pending, "服务正在暂停"),
                ServiceControllerStatus.ContinuePending => (CheckResult.Pending, "服务正在恢复"),
                _ => (CheckResult.Warn, "服务状态未知"),
            };
            SetEnvironmentCheck(_checkRdpIcon, _checkRdpText, level, text,
                $"本机远程桌面服务（TermService）：{text}。RDP 预览控件已就绪。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            SetEnvironmentCheck(_checkRdpIcon, _checkRdpText, CheckResult.Error, "服务查询失败",
                $"无法查询本机远程桌面服务（TermService）。{ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 将简短结果显示在首页，鼠标悬停可查看详情。同一故障持续存在期间只记录一次。
    /// </summary>
    private void SetEnvironmentCheck(Label? icon, Label text, CheckResult level, string result, string detail)
    {
        UpdateCheckItem(icon, text, level, result);
        if (_commandHints.GetToolTip(text) == detail) return;
        _commandHints.SetToolTip(text, detail);
        if (level == CheckResult.Error) AppendLog($"{text.AccessibleName}：{detail}", LogLevel.Error);
    }
}
