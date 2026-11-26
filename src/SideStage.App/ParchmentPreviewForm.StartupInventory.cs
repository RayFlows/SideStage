/*
 * 文件职责：在高级设置的启动项子页中维护自动扫描开关、手动刷新和最近结果。
 * 生命周期：首次进入时建立完整缓存，启动检查及手动刷新共用后台任务，离开子页后仍可完成。
 */
using System.Diagnostics;
using System.Text.Json;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private readonly StartupInventory _startupInventory;
    private StartupSnapshot? _startupSnapshot;
    private Button? _startupInventoryRow;
    private PaperScrollPage? _startupPage;
    private Label? _startupScanStatus;
    private TableLayoutPanel? _startupEntries;
    private SettingsActionRow? _scanNow;
    private bool _scanningStartupItems;
    private string? _startupScanNotice;
    private bool _showReadOnly;

    /// <summary>进入启动项子页。有快照时立即展示；首次进入时建立清单，之后可随时手动刷新。</summary>
    private void OpenStartupInventory(object? sender, EventArgs e)
    {
        LeaveDrawerPage();
        var preferences = _startupInventory.Preferences();
        _startupSnapshot ??= _startupInventory.Cached();
        _startupPage = new PaperScrollPage { Dock = DockStyle.Fill };
        var content = StartupStack();
        content.SuspendLayout();
        _startupPage.SetPageContent(content);
        content.Controls.Add(SectionHeading("扫描设置", compact: true));
        var manual = new SettingsList();
        _scanNow = new SettingsActionRow("立即扫描", _bodyFont, async (_, _) => await ScanStartupItemsAsync());
        manual.AddRow(_scanNow);
        content.Controls.Add(manual);
        _startupScanStatus = SettingsCaption.Footnote("", _labelFont);
        content.Controls.Add(_startupScanStatus);
        var automatic = new SettingsList();
        var toggle = new SettingsSwitch("启动时自动扫描", _bodyFont) { Checked = preferences.OnLaunch };
        automatic.AddRow(toggle);
        toggle.CheckedChanged += (_, _) =>
        {
            try { _startupInventory.SavePreferences(new(toggle.Checked)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _startupScanNotice = "无法保存扫描设置。";
                UpdateStartupScanStatus();
            }
        };
        content.Controls.Add(automatic);
        content.Controls.Add(SectionHeading("分身登录时启动"));
        content.Controls.Add(SettingsCaption.Footnote("关闭开关后，下次分身登录时会跳过该入口。应用自身是否允许重复启动未知。", _labelFont));
        _startupEntries = StartupStack();
        content.Controls.Add(_startupEntries);
        content.ResumeLayout(true);
        _overview!.Visible = false;
        _drawerPages!.Controls.Add(_startupPage);
        _drawerTitle!.Text = "分身启动项";
        _drawerBack!.Visible = true;
        _drawerBack.Focus();
        RenderStartupItems();
        UpdateStartupScanStatus();
        if (_startupSnapshot == null && _startupScanNotice == null) _ = ScanStartupItemsAsync();
    }

    /// <summary>创建随侧栏可用宽度和 DPI 调整的单列内容区。</summary>
    private static TableLayoutPanel StartupStack()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty, ColumnCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    /// <summary>启动与手动入口共用一次完整扫描；合并并发请求，扫描并写入成功后替换当前列表。</summary>
    private async Task ScanStartupItemsAsync()
    {
        if (_scanningStartupItems) return;
        _startupSnapshot ??= _startupInventory.Cached();
        _scanningStartupItems = true;
        _startupScanNotice = "正在扫描启动项…";
        UpdateStartupScanStatus();
        var lifetime = _updateLifetime.Token;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() => _startupInventory.ScanAsync(lifetime), lifetime);
            if (lifetime.IsCancellationRequested) return;
            _startupSnapshot = result;
            _startupScanNotice = null;
            RenderStartupItems();
            AppendLog($"启动项扫描完成，共 {result.Items.Count()} 条，耗时 {elapsed.Elapsed.TotalSeconds:F1} 秒。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or TimeoutException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            if (lifetime.IsCancellationRequested) return;
            _startupScanNotice = "扫描失败，可手动重试。";
            AppendLog($"启动项扫描失败：{ex.Message}", LogLevel.Warning);
        }
        finally
        {
            _scanningStartupItems = false;
            if (!lifetime.IsCancellationRequested) UpdateStartupScanStatus();
        }
    }

    /// <summary>显示当前操作、最近成功时间和数量。离开子页后跳过已释放的控件。</summary>
    private void UpdateStartupScanStatus()
    {
        if (_startupPage == null) return;
        _scanNow!.Enabled = !_scanningStartupItems;
        _startupEntries!.Enabled = !_scanningStartupItems;
        var previous = _startupSnapshot == null ? "" : $"上次扫描 {_startupSnapshot.Completed.LocalDateTime:yyyy-MM-dd HH:mm:ss}，共 {_startupSnapshot.Items.Count()} 条。";
        _startupScanStatus!.Text = string.Join(Environment.NewLine, new[] { _startupScanNotice, previous }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>可操作应用排在最前。只读登记可展开查看，每条展示其来源和保留原因。</summary>
    private void RenderStartupItems()
    {
        if (_startupPage == null || _startupSnapshot == null) return;
        var entries = _startupEntries!;
        entries.SuspendLayout();
        try
        {
            while (entries.Controls.Count > 0) entries.Controls[0].Dispose();
            var items = _startupSnapshot.Items.OrderBy(item => item.DisplayName ?? item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var controls = new SettingsList();
            entries.Controls.Add(controls);
            foreach (var item in items.Where(item => item.ReadOnlyReason == null || item.Blocked))
            {
                StartupEntry current = item;
                controls.AddRow(new StartupAppRow(item, _bodyFont, _labelFont, _commandHints, row => ChangeStartupRule(row, ref current)));
            }
            if (controls.RowCount == 0) entries.Controls.Add(SettingsCaption.Footnote("当前没有可单独调整的个人启动项。", _labelFont));
            var readOnly = items.Where(item => item.ReadOnlyReason != null && !item.Blocked).ToArray();
            if (readOnly.Length > 0)
            {
                var system = new SettingsList();
                entries.Controls.Add(system);
                string caption = _showReadOnly ? "收起只读项目" : $"查看只读项目（{readOnly.Length}）";
                system.AddRow(new SettingsActionRow(caption, _bodyFont, (_, _) =>
                { _showReadOnly = !_showReadOnly; RenderStartupItems(); }, disclosure: !_showReadOnly));
                if (_showReadOnly)
                    foreach (var item in readOnly) system.AddRow(new StartupAppRow(item, _bodyFont, _labelFont, _commandHints, _ => { }));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            entries.Controls.Add(SettingsCaption.Footnote("读取启动登记失败，请重新扫描。", _labelFont));
            AppendLog($"读取启动登记失败：{ex.Message}", LogLevel.Warning);
        }
        finally { entries.ResumeLayout(true); }
    }

    /// <summary>成功写入并回读后才移动滑块。目标已被其他程序更新时，保留它的新登记并刷新界面。</summary>
    private void ChangeStartupRule(StartupAppRow row, ref StartupEntry current)
    {
        try
        {
            current = _startupInventory.Registrations.SetAllowed(current, current.Blocked);
            row.Checked = !current.Blocked;
            _startupSnapshot = _startupInventory.Remember(_startupSnapshot!, current);
            // 旧托管登记恢复后可能成为只读项，按刚回读的 Windows 状态重新分组。
            if (current.ReadOnlyReason != null) RenderStartupItems();
            AppendLog($"{current.Name}：{(current.Blocked ? "下次分身登录时跳过" : "已恢复原启动项")}。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            AppendLog($"无法修改 {current.Name}：{error.Message}", LogLevel.Warning);
            _ = ScanStartupItemsAsync();
        }
    }
}
