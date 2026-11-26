/*
 * 文件职责：管理启动检查与手动检查、更新窗口、版本跳过偏好和退出交接；联网能力只属于桌面应用。
 * 生命周期：连接期间延后主动提示；明确同意后只断开 RDP，绝不借普通关闭流程注销子会话。
 * 持久化：当前用户可关闭启动检查或跳过单一版本，不创建后台服务或计划任务。
 */
using Microsoft.Win32;
using System.ComponentModel;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private const string UpdatePreferenceKey = StartupElevation.SettingsKey;
    private const string StartupPreferenceName = "CheckUpdatesOnStartup";
    private readonly ReleaseUpdateFeed _updates;
    private readonly string _updatePreferenceKey;
    private readonly CancellationTokenSource _updateLifetime = new();
    private SettingsActionRow? _checkUpdates;
    private SettingsSwitch? _startupUpdates;
    private Label? _updateStatus;
    private Button? _homeSettings;
    private ReleaseOffer? _updateOffer;
    private bool _checkingUpdates;
    private bool _pendingUpdatePrompt, _preparingUpdate, _exitingForUpdate;
    private ReleaseUpdateWindow? _updateWindow;
    private readonly bool _canReturnToLauncher;
    internal bool ContinueUpdateInLauncher { get; private set; }

    /// <summary>复用现有设置行。离线试装版仍可查看当前版本，但不提供不能完成的联网操作。</summary>
    private void AddUpdateSettings(TableLayoutPanel options)
    {
        options.Controls.Add(SectionHeading("应用更新"));
        SettingsList list = new();
        _checkUpdates = new SettingsActionRow("检查更新", _bodyFont, OpenOrCheckUpdate) { Enabled = _updates.IsConfigured };
        list.AddRow(_checkUpdates);
        _startupUpdates = new SettingsSwitch("启动时检查更新", _bodyFont)
        {
            Enabled = _updates.IsConfigured, Checked = _updates.IsConfigured && ReadStartupPreference(),
        };
        _startupUpdates.CheckedChanged += SaveStartupPreference;
        list.AddRow(_startupUpdates);
        options.Controls.Add(list);
        _updateStatus = SettingsCaption.Footnote("", _labelFont);
        options.Controls.Add(_updateStatus);
        SetUpdateStatus(_updates.IsConfigured ? "" : "本地试装版未连接发布仓库。");
    }

    /// <summary>仅在正规发布构建中读取用户偏好。首次默认检查；系统拒绝读取时不主动联网。</summary>
    private bool ReadStartupPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_updatePreferenceKey);
            return key?.GetValue(StartupPreferenceName) is not 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException) { return false; }
    }

    /// <summary>按用户操作写入开关，持久化失败时在同一区域说明，不影响当前会话。</summary>
    private void SaveStartupPreference(object? sender, EventArgs e)
    {
        if (_startupUpdates?.Checked != true) _pendingUpdatePrompt = false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_updatePreferenceKey);
            key.SetValue(StartupPreferenceName, _startupUpdates!.Checked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            SetUpdateStatus("本次选择未能保存，下次启动将沿用原设置。");
        }
    }

    /// <summary>手动检查不受「跳过此版本」影响，再次打开现有窗口会保留其下载进度。</summary>
    private async void OpenOrCheckUpdate(object? sender, EventArgs e)
    {
        if (_updateOffer == null) { await CheckUpdatesAsync(); return; }
        ShowUpdateOffer(_updateOffer);
    }

    /// <summary>单窗口同一时刻只发起一次检查。超时、断网或频道数据不完整都不会被当作最新版。</summary>
    private async Task CheckUpdatesAsync(bool startup = false)
    {
        if (_checkingUpdates || !_updates.IsConfigured || _checkUpdates == null) return;
        _checkingUpdates = true;
        _checkUpdates.Enabled = false;
        SetUpdateStatus("正在检查更新…");
        CancellationToken token = _updateLifetime.Token;
        try
        {
            var offer = await _updates.CheckAsync(token);
            if (token.IsCancellationRequested) return;
            _updateOffer = offer;
            if (offer == null) { SetUpdateStatus("未发现可用的新版本。"); return; }
            _checkUpdates.Text = "查看可用更新";
            if (_homeSettings != null) _homeSettings.Text = "设置 · 有更新";
            SetUpdateStatus($"可更新至 {offer.Version.Text}。");
            AppendLog($"发现 SideStage {offer.Version.Text}。");
            if (!startup) ShowUpdateOffer(offer);
            else if (_startupUpdates?.Checked == true && !IsSkipped(offer.Version.Text))
            {
                _pendingUpdatePrompt = true;
                TryPresentStartupUpdate();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException or InvalidDataException or IOException)
        {
            if (!token.IsCancellationRequested) SetUpdateStatus("未能完成检查，请稍后重试。");
        }
        finally
        {
            _checkingUpdates = false;
            if (!token.IsCancellationRequested && !_checkUpdates.IsDisposed) _checkUpdates.Enabled = true;
        }
    }

    /// <summary>无论成功、失败还是离线，都在结果旁保留当前正在运行的准确版本。</summary>
    private void SetUpdateStatus(string status)
    {
        if (_updateStatus != null) _updateStatus.Text = $"当前版本：{_updates.Current.Text}" +
            (status.Length == 0 ? "" : Environment.NewLine + status);
    }

    /// <summary>只为自动检查读取被跳过的精确版本；注册表不可读时不主动弹窗打扰用户。</summary>
    private bool IsSkipped(string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_updatePreferenceKey);
            return Equals(key?.GetValue("SkippedUpdate"), version);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException) { return true; }
    }

    /// <summary>推迟迟到的联网结果，直到用户回到本窗口且没有正在使用的远程桌面。</summary>
    private void TryPresentStartupUpdate()
    {
        if (!_pendingUpdatePrompt || _updateOffer == null || ActiveForm != this || !Enabled ||
            WindowState == FormWindowState.Minimized || _confirmingClose || UpdateConnectionActive()) return;
        ShowUpdateOffer(_updateOffer);
    }

    /// <summary>更新只需知道是否可能影响预览，不查询登录身份、不核验也不注销子会话。</summary>
    private bool UpdateConnectionActive() => _audioReconnect.Active || (_rdpPrepared &&
        _previewPanel.PreviewControl.GetConnectionState() != RdpPreviewConnectionState.Disconnected);

    /// <summary>使用非模态的自有更新窗口。关闭窗口或下载完成不会触发安装。</summary>
    private void ShowUpdateOffer(ReleaseOffer offer)
    {
        _pendingUpdatePrompt = false;
        _updateWindow ??= new(_updates.Current, offer,
            (source, progress, cancel) => new UpdateDownload().FetchAsync(offer, source, _updates.SigningKey, progress, cancel),
            InstallUpdateAsync, () =>
            {
                try { using var key = Registry.CurrentUser.CreateSubKey(_updatePreferenceKey); key.SetValue("SkippedUpdate", offer.Version.Text); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
                { SetUpdateStatus("本次已跳过，未能保存到下次启动。"); }
            }, _updateLifetime.Token, () =>
            {
                // 后台下载完成只更新入口；不抢焦点，也不借通知直接开始安装。
                if (_checkUpdates != null) _checkUpdates.Text = "查看待安装更新";
                if (_homeSettings != null) _homeSettings.Text = "设置 · 待安装";
                SetUpdateStatus($"{offer.Version.Text} 已下载，等待安装。");
            }, enterStandardMode: _canReturnToLauncher || StartupElevation.IsAdministrator() ? EnterStandardUpdateModeAsync : null);
        if (!_updateWindow.Visible)
        {
            _updateWindow.StartPosition = FormStartPosition.Manual;
            _updateWindow.Location = new Point(Left + Math.Max(0, (Width - _updateWindow.Width) / 2),
                Top + Math.Max(0, (Height - _updateWindow.Height) / 2));
            _updateWindow.Show(this);
        }
        _updateWindow.Activate();
    }

    /// <summary>
    /// 让原始普通权限启动器接手更新。已连接时先确认并仅断开预览，关闭被取消则保持现有窗口。
    /// 手动从高权限环境独立启动时，告知用户如何从开始菜单进入普通模式。
    /// </summary>
    private async Task EnterStandardUpdateModeAsync()
    {
        if (!_canReturnToLauncher)
            throw new InvalidOperationException("请从开始菜单正常打开 SideStage，在授权提示中选“否”，再点「检查更新」。更新前请先断开正在使用的连接并退出管理员窗口。");
        if (_preparingUpdate) return;
        _preparingUpdate = true;
        try
        {
            Enabled = false;
            ContinueUpdateInLauncher = await CloseForUpdateAsync();
        }
        finally
        {
            _exitingForUpdate = false;
            _preparingUpdate = false;
            if (!IsDisposed) Enabled = true;
        }
    }

    /// <summary>等待更新器就绪后再断开；若取消、断开超时或关闭被否决，管道不提交，安装便不会开始。</summary>
    private async Task InstallUpdateAsync(VerifiedUpdate update)
    {
        if (_preparingUpdate) return;
        _preparingUpdate = true;
        try
        {
            Enabled = false;
            using var handoff = await UpdateHandoff.PrepareAsync(update, _updateLifetime.Token);
            if (!await CloseForUpdateAsync()) return;
            try { handoff.Commit(); }
            catch (IOException)
            {
                // 原窗口已退出，不能再用其状态标签报告管道失败。
                MessageBox.Show("更新器提前退出，未能提交安装请求。请重新打开 SideStage 后重试。", "SideStage 更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _preparingUpdate = _exitingForUpdate = false;
            if (!IsDisposed) Enabled = true;
        }
    }

    /// <summary>取得明确同意后断开预览并保留子会话。更新器和普通权限启动器只有在窗口实际退出后才能接手。</summary>
    private async Task<bool> CloseForUpdateAsync()
    {
        if (UpdateConnectionActive() && MessageBox.Show(_updateWindow,
            "断开当前 RDP 连接，保留子会话并进入更新？", "断开连接并更新",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        CancelParentRecovery();
        var preview = _previewPanel.PreviewControl;
        await DisconnectForUpdateAsync(_audioReconnect.Cancel,
            () => _rdpPrepared && preview.GetConnectionState() != RdpPreviewConnectionState.Disconnected,
            preview.DisconnectPreview, _updateLifetime.Token);
        _exitingForUpdate = true;
        Close();
        if (!IsDisposed) throw new InvalidOperationException("窗口未退出，已取消更新。");
        return true;
    }

    /// <summary>停止音频自动重连，仅请求 ActiveX 断开并等待其确认；不调用任何会话终止接口。</summary>
    internal static async Task DisconnectForUpdateAsync(Func<bool> cancelReconnect, Func<bool> connected,
        Action disconnect, CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        bool alreadyRequested = cancelReconnect();
        if (!alreadyRequested && connected()) disconnect();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (connected())
            await Task.Delay(100, timeout.Token);
    }
}
