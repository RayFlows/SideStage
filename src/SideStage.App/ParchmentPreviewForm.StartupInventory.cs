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
    private PaperInfoButton? _startupPrivilegeWarning;
    private TableLayoutPanel? _startupEntries;
    private SettingsActionRow? _scanNow;
    private SettingsActionRow? _openAllStartup, _closeAllStartup;
    private bool _changingStartupItems;
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
        content.Controls.Add(CreateStartupItemsHeading());
        content.Controls.Add(SettingsCaption.Footnote("关闭开关后，下次登录子会话时会跳过该入口。应用自身是否允许重复启动未知。", _labelFont));
        var bulk = new SettingsList();
        _openAllStartup = new SettingsActionRow("全部打开", _bodyFont, async (_, _) => await SetAllStartupAsync(true));
        _closeAllStartup = new SettingsActionRow("全部关闭", _bodyFont, async (_, _) => await SetAllStartupAsync(false));
        bulk.AddRow(_openAllStartup);
        bulk.AddRow(_closeAllStartup);
        content.Controls.Add(bulk);
        _startupEntries = StartupStack();
        content.Controls.Add(_startupEntries);
        content.ResumeLayout(true);
        _overview!.Visible = false;
        _drawerPages!.Controls.Add(_startupPage);
        _drawerTitle!.Text = "子会话启动项";
        _drawerBack!.Visible = true;
        _drawerBack.Focus();
        RenderStartupItems();
        UpdateStartupScanStatus();
        if (_startupSnapshot == null && _startupScanNotice == null) _ = ScanStartupItemsAsync();
    }

    /// <summary>将权限提示紧邻分组标题，首次进入即可查看。提示读取本次运行的权限，与扫描完成时间无关。</summary>
    private TableLayoutPanel CreateStartupItemsHeading()
    {
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        DrawerMetrics.Margin(heading, 0, SectionGap, 0, 0);
        heading.Controls.Add(SectionHeading("子会话登录时启动", compact: true), 0, 0);
        _startupPrivilegeWarning = PaperInfoButton.PrivilegeWarning(_drawerArrowFont, _labelFont, "启动项权限提示",
            "需要管理员权限才能调整的启动项开关已隐藏。请以管理员身份重新打开 SideStage，查看可调整的启动项。");
        _startupPrivilegeWarning.Visible = _administrator == false;
        heading.Controls.Add(_startupPrivilegeWarning, 1, 0);
        return heading;
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
    private async Task ScanStartupItemsAsync(string? completedNotice = null)
    {
        if (_scanningStartupItems || _changingStartupItems) return;
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
            _startupScanNotice = completedNotice;
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
        bool idle = !_scanningStartupItems && !_changingStartupItems;
        _scanNow!.Enabled = idle;
        _startupEntries!.Enabled = idle;
        _openAllStartup!.Enabled = idle && _startupSnapshot != null && new StartupRuleBatch(_startupSnapshot.Items, true).Candidates.Length > 0;
        _closeAllStartup!.Enabled = idle && _startupSnapshot != null && new StartupRuleBatch(_startupSnapshot.Items, false).Candidates.Length > 0;
        var previous = _startupSnapshot == null ? "" : $"上次扫描 {_startupSnapshot.Completed.LocalDateTime:yyyy-MM-dd HH:mm:ss}，共 {_startupSnapshot.Items.Count()} 条。";
        _startupScanStatus!.Text = string.Join(Environment.NewLine, new[] { _startupScanNotice, previous }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>从确认时的快照逐项提交。写入在后台执行，结束后一次更新列表、摘要和失败明细。</summary>
    private async Task SetAllStartupAsync(bool allowed)
    {
        if (_startupSnapshot == null || _changingStartupItems || _scanningStartupItems) return;
        var batch = new StartupRuleBatch(_startupSnapshot.Items, allowed);
        if (batch.Candidates.Length == 0) return;
        string title = allowed ? "全部打开" : "全部关闭";
        string action = allowed ? "撤销子会话的跳过规则，沿用 Windows 的启动设置" : "在下次登录子会话时跳过";
        if (!PaperDialog.Confirm(this, $"{title}子会话启动项？",
            $"将对 {batch.Candidates.Length} 项{action}。其余 {batch.Skipped} 项跳过。", title)) return;
        _changingStartupItems = true;
        _startupScanNotice = $"正在处理 {batch.Candidates.Length} 个启动项…";
        UpdateStartupScanStatus();
        var registrations = _startupInventory.Registrations;
        try
        {
            await Task.Run(() => batch.Apply(registrations));
            if (IsDisposed || Disposing) return;
            if (batch.Repairs.Count != 0)
            {
                var approved = ConfirmStartupRepairs(this, batch.Repairs);
                await Task.Run(() => batch.FinishRepairs(registrations, approved));
            }
            if (IsDisposed || Disposing) return;
            _startupSnapshot = _startupInventory.Remember(_startupSnapshot!, batch.Changed);
            _startupScanNotice = $"{title}：成功 {batch.Changed.Count} 项，跳过 {batch.Skipped} 项，失败 {batch.Failures.Count} 项。";
            AppendLog(_startupScanNotice, batch.Failures.Count == 0 ? LogLevel.Info : LogLevel.Warning);
            foreach (string failure in batch.Failures) AppendLog(failure, LogLevel.Warning);
            RenderStartupItems();
        }
        catch (Exception ex) when (StartupRuleBatch.Recoverable(ex))
        {
            _startupScanNotice = $"请重新扫描以获取实际状态。{ex.Message}";
            AppendLog(_startupScanNotice, LogLevel.Warning);
        }
        finally
        {
            _changingStartupItems = false;
            if (!IsDisposed && !Disposing) UpdateStartupScanStatus();
        }
        // 有过期登记时以新扫描纠正该行，同时保留这一批的完成摘要。
        if (batch.Failures.Count > 0 && !IsDisposed && !Disposing) await ScanStartupItemsAsync(_startupScanNotice);
    }

    /// <summary>批量选择只针对本次任务和旧账户。默认全部未选，取消后保留每个原触发器。</summary>
    internal static HashSet<StartupRuleBatch.Repair> ConfirmStartupRepairs(IWin32Window owner, List<StartupRuleBatch.Repair> repairs)
    {
        using var dialog = new PaperDialog(Program.DisplayName, "修复登录任务",
            "勾选要修复的任务。恢复启动开关后，任务仍使用新账户。", width: 520);
        var list = new CheckedListBox { Dock = DockStyle.Top, CheckOnClick = true, HorizontalScrollbar = true,
            AccessibleName = "需要修复的登录任务", IntegralHeight = false, BorderStyle = BorderStyle.None,
            BackColor = PaperFieldSurface.Paper, ForeColor = PaperFieldSurface.Ink,
            Height = Math.Min(8, repairs.Count) * (dialog.Font.Height + 4) + 20, Margin = new Padding(0, 12, 0, 0) };
        foreach (var repair in repairs) list.Items.Add(repair, false);
        dialog.AddOptions(list);
        dialog.AddConfirmation("应用所选", "全部跳过");
        return dialog.ShowDialog(owner) == DialogResult.OK ? list.CheckedItems.Cast<StartupRuleBatch.Repair>().ToHashSet() : [];
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
            controls.SuspendLayout();
            foreach (var item in items.Where(item => item.ReadOnlyReason == null || item.Blocked))
            {
                StartupEntry current = item;
                controls.AddRow(new StartupAppRow(item, _bodyFont, _labelFont, _commandHints, row => ChangeStartupRule(row, ref current)));
            }
            entries.Controls.Add(controls);
            controls.ResumeLayout(true);
            if (controls.RowCount == 0) entries.Controls.Add(SettingsCaption.Footnote("当前没有可单独调整的个人启动项。", _labelFont));
            var readOnly = items.Where(item => item.ReadOnlyReason != null && !item.Blocked).ToArray();
            if (readOnly.Length > 0)
            {
                var system = new SettingsList();
                system.SuspendLayout();
                string caption = _showReadOnly ? "收起只读项目" : $"查看只读项目（{readOnly.Length}）";
                system.AddRow(new SettingsActionRow(caption, _bodyFont, (_, _) =>
                { _showReadOnly = !_showReadOnly; RenderStartupItems(); }, disclosure: !_showReadOnly));
                if (_showReadOnly)
                    foreach (var item in readOnly) system.AddRow(new StartupAppRow(item, _bodyFont, _labelFont, _commandHints, _ => { }));
                entries.Controls.Add(system);
                system.ResumeLayout(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            entries.Controls.Add(SettingsCaption.Footnote("读取启动登记失败，请重新扫描。", _labelFont));
            AppendLog($"读取启动登记失败：{ex.Message}", LogLevel.Warning);
        }
        finally
        {
            entries.ResumeLayout(true);
            // 子页已经可见时，分组的初次布局可能发生在滚动区分配最终宽度之前。
            entries.PerformLayout();
        }
    }

    /// <summary>成功写入并回读后才移动滑块。目标已被其他程序更新时，保留它的新登记并刷新界面。</summary>
    private void ChangeStartupRule(StartupAppRow row, ref StartupEntry current)
    {
        try
        {
            var registrations = _startupInventory.Registrations;
            try { current = registrations.SetAllowed(current, current.Blocked); }
            catch (StartupTriggerRepairRequired repair)
            {
                if (!PaperDialog.Confirm(this, "修复登录任务",
                    $"「{current.DisplayName ?? current.Name}」的登录任务引用了 Windows 无法识别的账户「{repair.Previous}」。\n\n" +
                    $"将登录用户修复为「{repair.Current}」并应用这次开关设置？恢复开关后，任务仍使用修复后的账户。",
                    "修复并应用")) return;
                current = registrations.SetAllowed(current, current.Blocked, repair.Previous);
            }
            row.Checked = !current.Blocked;
            _startupSnapshot = _startupInventory.Remember(_startupSnapshot!, current);
            // 旧托管登记恢复后可能成为只读项，按刚回读的 Windows 状态重新分组。
            if (current.ReadOnlyReason != null) RenderStartupItems();
            _startupScanNotice = null;
            UpdateStartupScanStatus();
            AppendLog($"{current.Name}：{(current.Blocked ? "下次登录子会话时跳过" : "已恢复原启动项")}。");
        }
        catch (Exception error) when (StartupRuleBatch.Recoverable(error))
        {
            AppendLog($"无法修改 {current.Name}：{error.Message}", LogLevel.Warning);
            if (error is UnauthorizedAccessException or System.Security.SecurityException
                or System.ComponentModel.Win32Exception { NativeErrorCode: 5 } || error.HResult == unchecked((int)0x80070005))
            {
                _startupScanNotice = $"Windows 未允许当前账户修改「{current.DisplayName ?? current.Name}」。";
                UpdateStartupScanStatus();
            }
            else _ = ScanStartupItemsAsync();
        }
    }
}
