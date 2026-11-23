/*
 * 文件职责：在高级设置显示当前版本、启动检查选项和检查结果；有更新时由用户决定是否打开发布页。
 * 生命周期：后台检查不抢焦点，关闭窗体取消请求；任何更新动作均不操作 RDP 或 Windows 登录凭据。
 * 持久化：仅在用户切换开关时写入当前用户的一项首选项，不创建后台服务或计划任务。
 */
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Text.Json;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private const string UpdatePreferenceKey = @"Software\SideStage";
    private const string StartupPreferenceName = "CheckUpdatesOnStartup";
    private readonly GitHubUpdateFeed _updates = GitHubUpdateFeed.FromBuild();
    private readonly CancellationTokenSource _updateLifetime = new();
    private SettingsActionRow? _checkUpdates;
    private SettingsSwitch? _startupUpdates;
    private Label? _updateStatus;
    private Button? _homeSettings;
    private ReleaseOffer? _updateOffer;
    private bool _checkingUpdates;

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
    private static bool ReadStartupPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UpdatePreferenceKey);
            return key?.GetValue(StartupPreferenceName) is not 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException) { return false; }
    }

    /// <summary>按用户操作写入开关，持久化失败时在同一区域说明，不影响当前会话。</summary>
    private void SaveStartupPreference(object? sender, EventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UpdatePreferenceKey);
            key.SetValue(StartupPreferenceName, _startupUpdates!.Checked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            SetUpdateStatus("本次选择未能保存，下次启动将沿用原设置。");
        }
    }

    /// <summary>只在用户点击已发现的更新时打开固定仓库的发布说明页，不调用安装程序。</summary>
    private async void OpenOrCheckUpdate(object? sender, EventArgs e)
    {
        if (_updateOffer == null) { await CheckUpdatesAsync(); return; }
        try { using var browser = Process.Start(new ProcessStartInfo(_updateOffer.Page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { SetUpdateStatus("无法打开默认浏览器，请稍后重试。"); }
    }

    /// <summary>单窗口同一时刻只发起一次检查。超时、断网或频道数据不完整都不会被当作最新版。</summary>
    private async Task CheckUpdatesAsync()
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
            _checkUpdates.Text = "查看更新并下载";
            if (_homeSettings != null) _homeSettings.Text = "设置 · 有更新";
            SetUpdateStatus($"可更新至 {offer.Version.Text}。");
            AppendLog($"发现 SideStage {offer.Version.Text}。可在高级设置中查看发布说明。");
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
}
