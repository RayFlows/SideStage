/*
 * 文件名称：ParchmentPreviewForm.cs
 * 文件职责：
 *   羊皮纸剪纸风格的桌面分身预览界面，承载 SideStage.App 的全部操作功能。
 *   未连接时显示环境检查；连接时由原生 RDP 面板占满铜框和标题栏以内的区域。
 *   功能完整性：与 ClassicPreviewForm 保持功能对等，不裁减任何会话操作。
 *
 * 安全边界：
 *   - 侧栏接收凭据后仅交给本机 RDP 控件或当前用户的 Windows 凭据管理器。
 *   - 关闭已连接的预览前确认注销目标，由 Windows 再核对父子关系。
 *   - 不在标题、日志或控件 Text 中暴露密码。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

/// <summary>羊皮纸剪纸风格的桌面分身预览窗口。</summary>
internal sealed partial class ParchmentPreviewForm : Form
{
    private static readonly Color CardLight   = Color.FromArgb(244, 238, 224);
    private static readonly Color FrameDark   = Color.FromArgb(73,  55,  42);
    private static readonly Color TextOnLight = Color.FromArgb(72,  48,  48);  // 浅底文字
    private static readonly Color TextMuted   = Color.FromArgb(103, 82, 65);  // 纸面上的次要文字
    private static readonly Color StatusWarn  = Color.FromArgb(192, 144, 72);  // 警告/等待
    private static readonly Color StatusErr   = Color.FromArgb(150, 80,  72);  // 错误

    // ── 服务 ─────────────────────────────────────────────────────────────
    private readonly ISessionInspector _sessionInspector;
    private readonly Func<Task> _enableChildSessions;
    private readonly WindowsChildSessionTerminator _sessionTerminator = new();
    private readonly WindowsChildSessionCredentialStore _credentialStore;
    private readonly string _logDirectory, _logFile;
    private readonly Action<string> _openFolder;
    private bool _logWriteFailed;
    private readonly ChildSessionPreviewPanel _previewPanel = new(ChildSessionPreviewSettings.Default,
        ChildSessionAudioMode.DoNotPlay, surface => new PaperScrollPage(surface) { BackColor = CardLight });
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    // ── UI 控件引用 ───────────────────────────────────────────────────────
    private Panel?  _centerCard;
    private PaperSurface? _homePaper;
    private Panel?  _settingsDrawer;       // 右侧滑出设置抽屉
    private DrawerCollapseTab? _drawerTab;
    private Panel?  _workspace;
    private Label?  _systemText;
    private EditionIndicator? _editionIndicator;
    private Label?  _checkCapabilityIcon;
    private Label?  _checkCapabilityText;
    private Label?  _checkParentIcon;
    private Label?  _checkParentText;
    private Label?  _connectionIcon, _connectionText;
    private Label?  _checkRdpIcon, _checkRdpText;
    private Label?  _checkCredentialIcon, _checkCredentialText;
    private Button? _enableChildButton, _configureCredentialsButton;
    private Button? _connectButton;
    private readonly ToolTip _commandHints = new();
    private Control? _sessionCommands;
    private Button? _credentialsRow;
    private Panel? _drawerPages, _overview;
    private Label? _drawerTitle;
    private Button? _drawerBack;
    private InlineCredentialEditor? _editor;
    private Button? _disconnectCommand, _logoffCommand;
    private ComboBox?        _resolutionSelector;
    private TextBox?         _customWidthSelector;
    private TextBox?         _customHeightSelector;
    private ComboBox?        _scalingModeSelector;
    private CheckBox?        _preserveAspectRatioToggle;
    private CheckBox?        _muteAudioToggle;
    private RichTextBox?     _eventLog;
    private SettingsGear?    _gear;
    private bool _canDisconnect;
    private bool _canLogoff;

    // ── 状态 ─────────────────────────────────────────────────────────────
    private bool _refreshInProgress;
    private readonly AudioPreviewReconnect _audioReconnect = new();
    private bool _childNeedsUnlock;
    private bool _synchronizingDisplaySettings;
    private string _lastVerifiedChild = string.Empty;
    private string _lastTransientStatus = string.Empty;
    private string? _lastPollError;
    private readonly Font _titleFont = TheatreTypography.Create(19, FontStyle.Bold);
    private readonly Font _drawerTitleFont = TheatreTypography.Create(13, FontStyle.Bold);
    private readonly Font _drawerArrowFont = new("Segoe MDL2 Assets", 12F);
    private readonly Font _sectionFont = TheatreTypography.Create(11, FontStyle.Bold);
    private readonly Font _bodyFont = TheatreTypography.Create(11);
    private readonly Font _labelFont = TheatreTypography.Create(9);

    /// <summary>创建默认验收窗口。初始状态只读取系统信息，由用户发起连接。</summary>
    public ParchmentPreviewForm() : this(new WindowsChildSessionCredentialStore()) { }

    /// <summary>建立实际主窗口。正式入口使用构建指定的更新源及用户偏好；界面验收可隔离凭据、发行数据和更新偏好。</summary>
    internal ParchmentPreviewForm(WindowsChildSessionCredentialStore credentialStore, string? logDirectory = null,
        Action<string>? openFolder = null, bool pollSessions = true, ISessionInspector? sessionInspector = null,
        Func<Task>? enableChildSessions = null, ReleaseUpdateFeed? updateFeed = null, string updatePreferenceKey = UpdatePreferenceKey)
    {
        _updates = updateFeed ?? ReleaseUpdateFeed.FromBuild();
        _updatePreferenceKey = updatePreferenceKey;
        _sessionInspector = sessionInspector ?? new WindowsSessionInspector();
        _enableChildSessions = enableChildSessions ?? ChildSessionEnableCommand.EnableAsync;
        _credentialStore = credentialStore;
        // 沿用首版日志目录，保证升级前后的「打开日志文件夹」指向同一段历史。
        _logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinChildDesk", "Logs");
        _logFile = Path.Combine(_logDirectory, $"preview-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        _openFolder = openFolder ?? OpenFolderInExplorer;
        Program.ApplyIdentity(this);
        BackColor = BronzeWindowFrame.OuterInk;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResizeRedraw = true;

        BuildWindowFrame();
        BuildLayout();
        BindBackgroundClicks(this);
        // 首次显示后才准备原生控件；从 Windows 凭据管理器切回时也能看到外部修改。
        Activated += RefreshOnActivation;
        Shown += (_, _) =>
        {
            ShowWindowsEdition();
            if (_checkCredentialText?.Text.Length == 0) RefreshCredentialCheck();
            RefreshSessionStatus();
            if (_startupUpdates?.Checked == true) _ = CheckUpdatesAsync(startup: true);
        };
        if (pollSessions) ConfigureRefreshTimer();
        RefreshSessionStatus();
        AppendLog($"{Program.DisplayName} 已就绪。");

        FormClosed += (_, _) =>
        {
            // 所属更新窗销毁时可能把焦点还给主窗；不能在提示控件释放后再刷新凭据。
            Activated -= RefreshOnActivation;
            _updateLifetime.Cancel();
            _updateLifetime.Dispose();
            _refreshTimer.Dispose();
            _commandHints.Dispose();
            _titleFont.Dispose();
            _drawerTitleFont.Dispose();
            _drawerArrowFont.Dispose();
            _sectionFont.Dispose();
            _bodyFont.Dispose();
            _labelFont.Dispose();
        };
    }

    /// <summary>切回仍在使用的主窗时刷新凭据并处理延后的更新提示；关闭期间的焦点归还不触发刷新。</summary>
    private void RefreshOnActivation(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || _confirmingClose || !IsHandleCreated) return;
        RefreshCredentialCheck();
        if (_pendingUpdatePrompt) BeginInvoke(TryPresentStartupUpdate);
    }

    /// <summary>窗口句柄重建时，重新设置系统菜单与外框合成属性。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InstallSessionMenu();
        ConfigureFrameCompositor();
        UpdateFrameGeometry();
    }

    // ── 布局 ─────────────────────────────────────────────────────────────
    /// <summary>在可伸缩的印刷框内承载首页、设置和原生远程桌面。</summary>
    private void BuildLayout()
    {
        Panel mainContainer = new() { Dock = DockStyle.Fill, BackColor = CardLight };
        _workspace = mainContainer;

        // 预览面板始终存在于控件树中，让 AxHost 能在首次连接前创建句柄。
        // 未连接时由中央卡片浮盖遮住它；不用 Visible=false 阻断 CreateControl。
        // 首页完全盖住预览。保留有效大小和原生句柄，展示连接画面时再跟随窗口拉伸。
        _previewPanel.Dock = DockStyle.None;
        _previewPanel.Size = mainContainer.ClientSize;
        _previewPanel.BorderStyle = BorderStyle.None;
        mainContainer.Controls.Add(_previewPanel);

        // 抽屉只在显式打开时覆盖预览，关闭后恢复原来的输入焦点。
        _settingsDrawer = BuildSettingsDrawer();
        _settingsDrawer.Visible = false;
        mainContainer.Controls.Add(_settingsDrawer);
        // 把手跨过侧栏左缘，作为工作区的同级窗口才能在原生预览上接收点击。
        _drawerTab = new DrawerCollapseTab(_settingsDrawer.BackColor, TextMuted, _labelFont) { Visible = false };
        _drawerTab.Click += ToggleSettings;
        mainContainer.Controls.Add(_drawerTab);
        mainContainer.DpiChangedAfterParent += (_, _) => PositionSettingsDrawer();

        // 中央卡片（未连接时）
        _centerCard = BuildCenterCard();
        mainContainer.Controls.Add(_centerCard);
        _centerCard.BringToFront();
        // 拖动途中也可能完成连接。显示状态一变就撤下纸缘，让原生画面或抽屉完整露出。
        _centerCard.VisibleChanged += (_, _) =>
        {
            _borderDrag?.Finish(cancel: false);
            RestorePaintedRim();
            UpdateFrameGeometry();
        };
        _settingsDrawer.VisibleChanged += (_, _) =>
        {
            RestorePaintedRim();
            if (!_settingsDrawer.Visible) LeaveCredentialPage();
            PositionSettingsDrawer();
            _drawerTab.Visible = _settingsDrawer.Visible;
            if (_drawerTab.Visible)
            {
                _settingsDrawer.BringToFront();
                _drawerTab.BringToFront();
            }
        };

        _gear = new SettingsGear { Visible = false };
        _gear.Click += ToggleSettings;
        mainContainer.Controls.Add(_gear);
        mainContainer.Resize += (_, _) =>
        {
            PositionSettingsDrawer();
            _gear.Reposition();
        };
        Controls.Add(mainContainer);
    }

    /// <summary>将会话检查和操作居中排在整张纸面上，保持中文字段和值便于扫读的行宽。</summary>
    private Panel BuildCenterCard()
    {
        const int checkCount = 6;
        // 纸页自己绘制整张背景。透明底只用于中央小表，拖动时无需逐层回调父窗口重画整页。
        PaperSurface container = new() { Margin = Padding.Empty, AutoScroll = true };
        _homePaper = container;
        PaperScrollPage viewport = new(container) { BackColor = container.BackColor };
        PaperSurface.BufferedTable card = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = Padding.Empty, BackColor = Color.Transparent,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = checkCount + 2,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < card.RowCount; row++)
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewport.SetPageContent(card);
        Label title = new()
        {
            Text = "环境检查",
            Font = _titleFont,
            ForeColor = TextOnLight,
            AutoSize = true,
            Anchor = AnchorStyles.None,
        };
        card.Controls.Add(title, 0, 0);
        card.SetColumnSpan(title, 3);
        _enableChildButton = MakeCheckAction("启用子会话", EnableChildSessions);
        _configureCredentialsButton = MakeCheckAction("配置登录凭据", ManageCredentials);
        _editionIndicator = new EditionIndicator(_labelFont, _drawerArrowFont);
        (_, _systemText) = AddCheckRow(card, 1, "操作系统", statusIcon: _editionIndicator);
        _systemText.Text = "正在读取…";
        (_checkCapabilityIcon, _checkCapabilityText) = AddCheckRow(card, 2, "子会话功能", _enableChildButton);
        (_checkRdpIcon, _checkRdpText) = AddCheckRow(card, 3, "本机远程桌面");
        (_checkParentIcon, _checkParentText) = AddCheckRow(card, 4, "主桌面");
        (_connectionIcon, _connectionText) = AddCheckRow(card, 5, "RDP 连接");
        (_checkCredentialIcon, _checkCredentialText) = AddCheckRow(card, 6, "登录凭据", _configureCredentialsButton);

        // 主连接按钮
        _connectButton = new PaperCommandButton
        {
            Text = "连接",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(138, 38),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = _sectionFont,
            FaceColor = Color.FromArgb(79, 64, 53),
            ForeColor = Color.FromArgb(252, 240, 212),
        };
        _connectButton.FlatAppearance.BorderSize = 0;
        _connectButton.Click += ConnectPreview;
        FlowLayoutPanel commands = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.None, Margin = Padding.Empty,
            WrapContents = false, BackColor = Color.Transparent };
        commands.Controls.Add(_connectButton);
        Button advanced = MakePaperCommand("高级设置", ToggleSettings);
        _homeSettings = advanced;
        advanced.AutoSize = true;
        advanced.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        advanced.MinimumSize = new Size(112, 38);
        advanced.Margin = Padding.Empty;
        commands.Controls.Add(advanced);
        card.Controls.Add(commands, 0, card.RowCount - 1);
        card.SetColumnSpan(commands, 3);
        ConfigureHomeLayout(container, card, title, commands);
        return viewport;
    }

    /// <summary>构造由状态色标、字段名和右对齐动态值组成的一行。</summary>
    private (Label Icon, Label Value) AddCheckRow(TableLayoutPanel table, int row, string caption, Button? action = null,
        EditionIndicator? statusIcon = null)
    {
        Label icon = (Label?)statusIcon ?? new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty, Font = _labelFont };
        icon.AutoSize = true;
        Label label = new() { AutoSize = true, Text = caption, Font = _sectionFont, ForeColor = TextMuted,
            Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
        Label value = new() { AccessibleName = caption + "状态", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = _bodyFont, ForeColor = TextOnLight };
        statusIcon?.BindHover(label);
        statusIcon?.BindHover(value);
        table.Controls.Add(icon, 0, row);
        table.Controls.Add(label, 1, row);
        if (action is null)
        {
            table.Controls.Add(value, 2, row);
        }
        else
        {
            // 隐藏已完成项目的按钮时自动收回末列，让结果文字仍与其余行的右缘对齐。
            TableLayoutPanel trailing = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent, ColumnCount = 2, RowCount = 1 };
            trailing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            trailing.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            trailing.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            trailing.Controls.Add(value, 0, 0);
            trailing.Controls.Add(action, 1, 0);
            table.Controls.Add(trailing, 2, row);
        }
        return (icon, value);
    }

    /// <summary>建立检查项右端的轻量操作按钮。具体检查给出结果后才显示相应入口。</summary>
    private Button MakeCheckAction(string caption, EventHandler click)
    {
        PaperCommandButton button = new()
        {
            Text = caption, Font = _labelFont, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 28),
            Anchor = AnchorStyles.Right, Margin = new Padding(9, 0, 3, 0), Visible = false,
            FaceColor = Color.FromArgb(230, 218, 192), ForeColor = PaperFieldSurface.Ink,
        };
        button.Click += click;
        return button;
    }

    /// <summary>创建可用键盘操作的次级纸面按钮，用于设置、返回和凭据入口。</summary>
    private Button MakePaperCommand(string text, EventHandler action)
    {
        PaperCommandButton button = new()
        {
            Text = text, Font = _bodyFont, ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent,
            Size = new Size(120, 36), Margin = new Padding(2, 2, 6, 2),
        };
        button.FlatAppearance.MouseOverBackColor = PaperFieldSurface.Hover;
        button.Click += action;
        return button;
    }

    /// <summary>打开或关闭同一套显示和声音控件，在预览前后保留一致的状态。</summary>
    private void ToggleSettings(object? sender, EventArgs e)
    {
        if (_settingsDrawer == null) return;
        _settingsDrawer.Visible = !_settingsDrawer.Visible;
        if (_gear != null) _gear.Visible = _centerCard?.Visible == false && !_settingsDrawer.Visible;
        if (_settingsDrawer.Visible)
        {
            _settingsDrawer.SelectNextControl(null, true, true, true, true);
        }
        else if (_centerCard?.Visible == true) _connectButton?.Focus();
        else
        {
            _gear?.BringToFront();
            _previewPanel.PreviewControl.Focus();
        }
    }

    /// <summary>在纸面设置栏中建立实际的分辨率、缩放、比例与声音控件。</summary>
    private Panel BuildCompactSettingsPanel()
    {
        SettingsList p = new();

        // 分辨率
        _resolutionSelector = new PaperComboBox("远端分辨率")
        {
            Font = _bodyFont,
        };
        _resolutionSelector.Items.AddRange(
            RemoteDesktopResolution.Common
                .Select(r => (object)new ResolutionOption(r.ToString(), r))
                .Append(new ResolutionOption("自定义…", null))
                .ToArray());
        p.AddRow(_resolutionSelector);

        _customWidthSelector = CreateDimensionInput();
        _customHeightSelector = CreateDimensionInput();
        p.AddRow(new SettingsInputRow("宽度", _customWidthSelector, _bodyFont) { Visible = false });
        p.AddRow(new SettingsInputRow("高度", _customHeightSelector, _bodyFont) { Visible = false });

        // 缩放
        _scalingModeSelector = new PaperComboBox("画面缩放")
        {
            Font = _bodyFont,
        };
        _scalingModeSelector.Items.AddRange(
        [
            new ScalingModeOption("自适应",    PreviewScalingMode.Adaptive),
            new ScalingModeOption("1:1 原像素", PreviewScalingMode.OneToOne),
        ]);
        p.AddRow(_scalingModeSelector);

        _preserveAspectRatioToggle = new SettingsSwitch("保持宽高比", _bodyFont) { Checked = true };
        p.AddRow(_preserveAspectRatioToggle);

        // 音频
        _muteAudioToggle = new SettingsSwitch("静音分身", _bodyFont) { Checked = true };
        p.AddRow(_muteAudioToggle);

        // 注册变更事件
        _resolutionSelector.SelectedIndexChanged += ChangeDisplaySettings;
        _scalingModeSelector.SelectedIndexChanged += ChangeDisplaySettings;
        _preserveAspectRatioToggle.CheckedChanged  += ChangeDisplaySettings;
        _muteAudioToggle.CheckedChanged            += ChangeAudioMode;

        SynchronizeDisplaySettingsControls(_previewPanel.Settings);
        return p;
    }

    /// <summary>把显示设置、静音和活动日志放在一处，首页和预览共用一个抽屉实例。</summary>
    private Panel BuildSettingsDrawer()
    {
        Panel drawer = new()
        {
            BackColor = Color.FromArgb(238, 228, 210),
        };
        DrawerMetrics.Bind(drawer, () => drawer.Padding = DrawerMetrics.Space(drawer, 18, 8, 18, 12));
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var logArea = new RowStyle(SizeType.Absolute);
        layout.RowStyles.Add(logArea);
        drawer.Controls.Add(layout);
        // 左右保留相同的导航宽度。返回键显隐时，标题的中心始终与侧栏中心重合。
        TableLayoutPanel heading = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute));
        _drawerBack = new PaperCommandButton
        {
            Text = "\uE72B", Font = _drawerArrowFont, AccessibleName = "返回高级设置",
            Circular = true, ForeColor = TextMuted, Anchor = AnchorStyles.Left,
            Margin = Padding.Empty, Visible = false,
        };
        _drawerBack.Click += (_, _) => LeaveCredentialPage();
        _commandHints.SetToolTip(_drawerBack, "返回高级设置");
        heading.Controls.Add(_drawerBack, 0, 0);
        _drawerTitle = new Label { Text = "高级设置", Font = _drawerTitleFont, ForeColor = TextOnLight, AutoSize = true,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
        DrawerMetrics.Bind(heading, () =>
        {
            int side = heading.LogicalToDeviceUnits(44);
            heading.ColumnStyles[0].Width = heading.ColumnStyles[2].Width = side;
            heading.MinimumSize = new Size(0, heading.LogicalToDeviceUnits(48));
            _drawerBack.Size = heading.LogicalToDeviceUnits(new Size(38, 38));
        });
        heading.Controls.Add(_drawerTitle, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        _drawerPages = new Panel { Dock = DockStyle.Fill };
        DrawerMetrics.Margin(_drawerPages, 0, 7, 0, 0);
        PaperScrollPage overview = new();
        _overview = overview;
        TableLayoutPanel options = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        overview.SetPageContent(options);
        _drawerPages.Controls.Add(_overview);
        options.Controls.Add(SectionHeading("画面与声音"));
        options.Controls.Add(BuildCompactSettingsPanel());
        options.Controls.Add(SettingsCaption.Footnote(
            "连接中切换静音会自动重连。缩放和宽高比即时生效，更改分辨率需先断开。", _labelFont));
        SettingsList account = new(16, 6);
        _credentialsRow = new SettingsActionRow("登录凭据", _bodyFont, ManageCredentials, disclosure: true);
        account.AddRow(_credentialsRow);
        options.Controls.Add(account);

        // 两个入口沿用系统菜单的状态与处理函数，注销前仍展示目标编号和默认取消的确认框。
        TableLayoutPanel session = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Margin = Padding.Empty, Visible = false };
        session.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sessionCommands = session;
        session.Controls.Add(SectionHeading("会话操作"));
        SettingsList commands = new();
        _disconnectCommand = new SettingsActionRow("断开连接", _bodyFont, DisconnectPreview);
        _logoffCommand = new SettingsActionRow("注销子会话…", _bodyFont, LogoffChildSession);
        _logoffCommand.ForeColor = Color.FromArgb(137, 65, 52);
        commands.AddRow(_disconnectCommand);
        commands.AddRow(_logoffCommand);
        session.Controls.Add(commands);
        options.Controls.Add(session);
        AddUpdateSettings(options);
        layout.Controls.Add(_drawerPages, 0, 1);
        TableLayoutPanel logHeading = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        DrawerMetrics.Bind(logHeading, () => logHeading.MinimumSize = new Size(0, logHeading.LogicalToDeviceUnits(35)));
        logHeading.Paint += (_, e) =>
        {
            int inset = logHeading.LogicalToDeviceUnits(PaperFieldSurface.TextInset);
            using Pen rule = new(Color.FromArgb(203, 187, 162));
            e.Graphics.DrawLine(rule, inset, 1, logHeading.ClientSize.Width - inset - 1, 1);
        };
        logHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logHeading.Controls.Add(SectionHeading("活动日志"), 0, 0);
        logHeading.Controls.Add(new ActivityLogInfoButton(_drawerArrowFont, _labelFont, OpenLogDirectory), 1, 0);
        layout.Controls.Add(logHeading, 0, 2);
        _eventLog = new ActivityLogView
        {
            AccessibleName = "活动日志", Dock = DockStyle.Fill, Margin = Padding.Empty, ReadOnly = true,
            WordWrap = true, ScrollBars = RichTextBoxScrollBars.None, DetectUrls = false,
            Font = _labelFont,
        };
        layout.Controls.Add(new PaperLogFrame(_eventLog), 0, 3);
        // 高窗口保留完整日志框；矮窗口最多分配四分之一给日志正文，让中间设置列表可以滚动。
        void SizeLogArea() => logArea.Height = Math.Min(layout.LogicalToDeviceUnits(136), layout.ClientSize.Height / 4);
        DrawerMetrics.Bind(layout, SizeLogArea);
        layout.SizeChanged += (_, _) => SizeLogArea();
        return drawer;
    }

    /// <summary>以统一的粗体字级和上下间距区分抽屉中的设置分组。</summary>
    private Label SectionHeading(string text)
    {
        SettingsCaption caption = new() { Text = text, AutoSize = true, Font = _sectionFont,
            Anchor = AnchorStyles.Left, ForeColor = TextOnLight };
        DrawerMetrics.Margin(caption, PaperFieldSurface.TextInset, 4, 0, 8);
        return caption;
    }

    /// <summary>为本窗口的非交互表面绑定点击行为。点纸面、标题或字段名时，结束输入并保留草稿。</summary>
    private void BindBackgroundClicks(Control surface)
    {
        if (surface == _previewPanel || surface is not (Panel or Label or Form)) return;
        surface.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ReleaseFieldFocus();
        };
        foreach (Control child in surface.Controls) BindBackgroundClicks(child);
    }

    /// <summary>只释放设置输入行的编辑焦点，文本框和数字框随后都可通过点击或 Tab 再次进入。</summary>
    private void ReleaseFieldFocus()
    {
        for (Control? current = ActiveControl; current != null; current = current.Parent)
        {
            if (current is not SettingsInputRow) continue;
            ActiveControl = null;
            return;
        }
    }

    /// <summary>右侧放置设置抽屉，首页保留独立可滚动的可见区域。实时预览仍占满工作区。</summary>
    private void PositionSettingsDrawer()
    {
        if (_workspace is null || _settingsDrawer is null || _centerCard is null) return;
        Size area = _workspace.ClientSize;
        if (!_settingsDrawer.Visible)
        {
            _centerCard.Dock = DockStyle.Fill;
            return;
        }
        int width = Math.Min(area.Width, LogicalToDeviceUnits(348));
        _settingsDrawer.Bounds = new Rectangle(area.Width-width, 0, width, area.Height);
        _drawerTab?.PlaceAt(_settingsDrawer);
        _centerCard.Dock = DockStyle.Left;
        _centerCard.Width = area.Width - width;
    }

    // ── 操作：连接 ───────────────────────────────────────────────────────
    private void ConnectPreview(object? sender, EventArgs e)
    {
        try
        {
            using WindowsChildSessionCredentials? saved = _credentialStore.Read();
            if (saved is not null)
            {
                _previewPanel.PreviewControl.ConnectPreview(saved);
                AppendLog("已使用本机保存的登录凭据请求连接子会话。");
            }
            else
            {
                OpenCredentialPage(connectAfterSubmit: true, hasSavedCredentials: false);
                return;
            }
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            ShowError("无法连接子会话", ex);
        }
        RefreshSessionStatus();
    }

    /// <summary>从设置行打开凭据编辑页，已有本机保存项时提供删除入口。</summary>
    private void ManageCredentials(object? sender, EventArgs e)
    {
        try
        {
            OpenCredentialPage(connectAfterSubmit: false, _credentialStore.HasSavedCredentials());
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            ShowError("无法管理本机登录凭据", ex);
        }
    }

    /// <summary>在同一侧栏切换到输入页，由打开入口决定是仅保存还是提交给本机 RDP。</summary>
    private void OpenCredentialPage(bool connectAfterSubmit, bool hasSavedCredentials)
    {
        if (_settingsDrawer is null || _drawerPages is null) return;
        if (!_settingsDrawer.Visible) ToggleSettings(this, EventArgs.Empty);
        LeaveCredentialPage();
        _editor = new InlineCredentialEditor(_bodyFont, _labelFont, connectAfterSubmit, hasSavedCredentials,
            (credentials, persist) =>
            {
                if (persist)
                {
                    _credentialStore.Save(credentials);
                    RefreshCredentialCheck();
                }
                if (connectAfterSubmit) _previewPanel.PreviewControl.ConnectPreview(credentials);
                AppendLog(connectAfterSubmit ? "已发出 RDP 连接请求。" : "已更新本机登录凭据。");
                LeaveCredentialPage();
                RefreshSessionStatus();
            }, () =>
            {
                _credentialStore.Delete();
                RefreshCredentialCheck();
                AppendLog("已删除本机登录凭据。", LogLevel.Warning);
            });
        _overview!.Visible = false;
        _drawerPages.Controls.Add(_editor);
        BindBackgroundClicks(_editor);
        _drawerTitle!.Text = "登录凭据";
        _drawerBack!.Visible = true;
        _drawerBack.Focus();
    }

    /// <summary>退出编辑页时销毁密码输入框，返回设置列表并恢复原来的键盘入口。</summary>
    private void LeaveCredentialPage()
    {
        if (_editor == null) return;
        _editor.Dispose();
        _editor = null;
        _overview!.Visible = true;
        _drawerTitle!.Text = "高级设置";
        _drawerBack!.Visible = false;
        if (_settingsDrawer?.Visible == true) _credentialsRow?.Focus();
    }

    // ── 操作：断开 ───────────────────────────────────────────────────────
    private void DisconnectPreview(object? sender, EventArgs e)
    {
        if (!_canDisconnect) return;
        bool wasAudioReconnect = _audioReconnect.Active;
        bool disconnectIssued = _audioReconnect.Cancel();
        try
        {
            if (!disconnectIssued && _previewPanel.PreviewControl.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
                _previewPanel.PreviewControl.DisconnectPreview();
            AppendLog(wasAudioReconnect
                ? "已取消音频设置的自动重连并断开连接；子会话不会注销。"
                : "已断开连接；子会话不会注销。");
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            ShowError("无法断开 RDP 连接", ex);
        }
        RefreshSessionStatus();
    }

    // ── 操作：注销 ───────────────────────────────────────────────────────
    private void LogoffChildSession(object? sender, EventArgs e)
    {
        if (!_canLogoff) return;
        try
        {
            var child = _sessionInspector.TryGetChildSession();
            if (child is not { IsChildSession: true, StateQueryError: null } ||
                child.Id == _sessionInspector.GetCurrentSession().Id) return;
            if (_previewPanel.PreviewControl.GetConnectionState() != RdpPreviewConnectionState.Disconnected &&
                _previewPanel.PreviewControl.VerifiedConnectionTarget != child.Id)
                throw new InvalidOperationException("连接的子会话已变化，请重新核对。");
            if (MessageBox.Show(this, $"注销子会话 {child.Id.Value}？子会话中的程序将全部结束，未保存的工作会丢失。",
                "确认注销子会话", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _audioReconnect.Cancel();
            _sessionTerminator.LogoffCurrentChildSession(child.Id);
            AppendLog($"Windows 已接受子会话 {child.Id.Value} 的注销请求。", LogLevel.Warning);
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            ShowError("无法注销子会话", ex);
        }
        RefreshSessionStatus();
    }

    /// <summary>收到第一次切换立即锁住开关。轮询确认新连接或报告失败后再恢复操作。</summary>
    private void ChangeAudioMode(object? sender, EventArgs e)
    {
        if (_muteAudioToggle == null || _audioReconnect.Active) return;
        ChildSessionAudioMode mode = _muteAudioToggle.Checked
            ? ChildSessionAudioMode.DoNotPlay
            : ChildSessionAudioMode.RedirectToThisComputer;
        try
        {
            _muteAudioToggle.Enabled = false;
            _audioReconnect.Change(_previewPanel.PreviewControl, mode);
            AppendLog(_audioReconnect.Active ? "正在切换分身声音，等待旧连接断开。" :
                _muteAudioToggle.Checked ? "分身声音已设为关闭。" : "分身声音已设为转发到本机。");
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            ShowError("无法更改分身声音设置", ex);
        }
        RefreshSessionStatus();
    }

    // ── 操作：显示设置 ───────────────────────────────────────────────────
    /// <summary>建立右对齐的像素输入框。用户可逐位输入或粘贴，回车或离开时才校验并应用。</summary>
    private TextBox CreateDimensionInput()
    {
        TextBox field = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Right, ImeMode = ImeMode.Disable };
        field.Leave += CommitDimension;
        field.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            CommitDimension(field, EventArgs.Empty);
        };
        return field;
    }

    /// <summary>校验单边像素值。非法输入恢复当前生效的尺寸，并在该字段提示允许的范围。</summary>
    private void CommitDimension(object? sender, EventArgs e)
    {
        if (_synchronizingDisplaySettings || sender is not TextBox field ||
            _resolutionSelector?.SelectedItem is not ResolutionOption { Resolution: null }) return;
        RemoteDesktopResolution current = _previewPanel.Settings.Resolution;
        int active = field == _customWidthSelector ? current.Width : current.Height;
        if (!int.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pixels) ||
            pixels is < 200 or > 8192)
        {
            field.Text = active.ToString(CultureInfo.InvariantCulture);
            if (field.Visible) _commandHints.Show("请输入 200 至 8192 之间的整数。", field, 0, field.Height, 2400);
            return;
        }
        field.Text = pixels.ToString(CultureInfo.InvariantCulture);
        if (pixels != active) ChangeDisplaySettings(field, EventArgs.Empty);
    }

    /// <summary>应用分辨率、缩放或宽高比。自定义行只提交当前字段，另一边沿用生效值。</summary>
    private void ChangeDisplaySettings(object? sender, EventArgs e)
    {
        if (_synchronizingDisplaySettings
            || _resolutionSelector?.SelectedItem is not ResolutionOption resOpt
            || _scalingModeSelector?.SelectedItem is not ScalingModeOption scaleOpt)
            return;
        try
        {
            RemoteDesktopResolution resolution = resOpt.Resolution ?? _previewPanel.Settings.Resolution;
            if (sender is TextBox field)
            {
                int pixels = int.Parse(field.Text, CultureInfo.InvariantCulture);
                resolution = field == _customWidthSelector ? new(pixels, resolution.Height) : new(resolution.Width, pixels);
            }
            ChildSessionPreviewSettings settings = new(
                resolution, scaleOpt.Mode, _preserveAspectRatioToggle?.Checked ?? true);
            _previewPanel.SetSettings(settings);
            SynchronizeDisplaySettingsControls(settings, keepCustomResolution: resOpt.Resolution is null);
            AppendLog($"画面设置：{settings.Resolution}，" +
                $"{(settings.ScalingMode == PreviewScalingMode.OneToOne ? "1:1" : "自适应")}，" +
                $"{(settings.PreserveAspectRatio ? "保持比例" : "铺满")}。");
        }
        catch (Exception ex) when (IsDisplayable(ex))
        {
            SynchronizeDisplaySettingsControls(_previewPanel.Settings);
            ShowError("无法更改画面设置", ex);
        }
        UpdateSettingsAvailability(_previewPanel.PreviewControl.GetConnectionState());
    }

    private void SynchronizeDisplaySettingsControls(
        ChildSessionPreviewSettings settings, bool keepCustomResolution = false)
    {
        if (_resolutionSelector == null || _scalingModeSelector == null) return;
        _synchronizingDisplaySettings = true;
        try
        {
            ResolutionOption? match = _resolutionSelector.Items.OfType<ResolutionOption>()
                .FirstOrDefault(o => o.Resolution == settings.Resolution);
            _resolutionSelector.SelectedItem = match is not null && !keepCustomResolution
                ? match
                : _resolutionSelector.Items.OfType<ResolutionOption>().Single(o => o.Resolution is null);
            if (_customWidthSelector != null) _customWidthSelector.Text = settings.Resolution.Width.ToString(CultureInfo.InvariantCulture);
            if (_customHeightSelector != null) _customHeightSelector.Text = settings.Resolution.Height.ToString(CultureInfo.InvariantCulture);
            _scalingModeSelector.SelectedItem = _scalingModeSelector.Items
                .OfType<ScalingModeOption>().Single(o => o.Mode == settings.ScalingMode);
            if (_preserveAspectRatioToggle != null)
                _preserveAspectRatioToggle.Checked = settings.PreserveAspectRatio;
        }
        finally { _synchronizingDisplaySettings = false; }
    }

    /// <summary>根据连接阶段更新可编辑性。自定义尺寸按整行显示或隐藏，使相邻分隔线随之收合。</summary>
    private void UpdateSettingsAvailability(RdpPreviewConnectionState state)
    {
        bool disconnected   = state == RdpPreviewConnectionState.Disconnected;
        bool canChangeScale = state != RdpPreviewConnectionState.Connecting;
        bool isCustom       = _resolutionSelector?.SelectedItem is ResolutionOption { Resolution: null };

        if (_resolutionSelector   != null) _resolutionSelector.Enabled  = disconnected;
        if (_customWidthSelector?.Parent is Control widthRow) { widthRow.Enabled = disconnected; widthRow.Visible = isCustom; }
        if (_customHeightSelector?.Parent is Control heightRow) { heightRow.Enabled = disconnected; heightRow.Visible = isCustom; }
        if (_scalingModeSelector  != null) _scalingModeSelector.Enabled  = canChangeScale;
        if (_preserveAspectRatioToggle != null)
            _preserveAspectRatioToggle.Enabled = canChangeScale &&
                _scalingModeSelector?.SelectedItem is ScalingModeOption { Mode: PreviewScalingMode.Adaptive };
    }

    // ── 轮询刷新 ─────────────────────────────────────────────────────────
    private void ConfigureRefreshTimer()
    {
        _refreshTimer.Interval = 500;
        _refreshTimer.Tick += (_, _) => RefreshSessionStatus();
        _refreshTimer.Start();
    }

    /// <summary>刷新环境检查和当前会话的操作权限。会话查询失败时收回原来的就绪提示。</summary>
    private void RefreshSessionStatus()
    {
        if (_refreshInProgress || _confirmingClose || IsDisposed) return;
        _refreshInProgress = true;
        bool? capability = null;
        try
        {
            RefreshLocalRdpCheck();
            bool enabled = _sessionInspector.IsChildSessionsEnabled();
            capability = enabled;
            UpdateCapabilityCheck(enabled);
            SessionSnapshot parent         = _sessionInspector.GetCurrentSession();
            SessionSnapshot? child         = _sessionInspector.TryGetChildSession();
            _childNeedsUnlock              = child?.IsLocked == true;
            RdpPreviewConnectionState prev = _rdpPrepared ? _previewPanel.PreviewControl.GetConnectionState()
                : RdpPreviewConnectionState.Disconnected;

            if (_audioReconnect.Active)
            {
                try
                {
                    if (_audioReconnect.Poll(_previewPanel.PreviewControl, child, _credentialStore) is { } progress)
                        AppendLog(progress);
                }
                catch (Exception ex) when (IsDisplayable(ex)) { AppendLog($"音频重连失败：{ex.Message}", LogLevel.Error); }
                prev = _previewPanel.PreviewControl.GetConnectionState();
            }

            // 环境检查指示
            UpdateCheckItem(_checkParentIcon, _checkParentText,
                parent.IsDesktopReady && parent.StateQueryError is null ? CheckResult.Ok : CheckResult.Warn,
                DescribeSession(parent));

            // 按钮可用性
            bool disconnected = prev == RdpPreviewConnectionState.Disconnected;
            if (_connectButton  != null) _connectButton.Enabled  = enabled && _rdpPrepared && disconnected && !_audioReconnect.Active;

            UpdateSettingsAvailability(prev);

            // 子会话核验
            bool verified = false;
            string? verificationMessage = null;
            if (prev == RdpPreviewConnectionState.Connected)
            {
                try
                {
                    SessionSnapshot verifiedChild = _previewPanel.PreviewControl.VerifyConnectedChildSession();
                    string msg = $"已核验目标子会话 {verifiedChild.Id}";
                    if (!string.Equals(_lastVerifiedChild, msg, StringComparison.Ordinal))
                    {
                        AppendLog(msg);
                        _lastVerifiedChild = msg;
                    }
                    verified = true;
                    _lastTransientStatus = string.Empty;
                }
                catch (InvalidOperationException ex)
                {
                    verificationMessage = ex.Message;
                    AppendTransientStatus(_previewPanel.IsPresentingChildSignIn
                        ? (_childNeedsUnlock ? "请在远程桌面中解锁。" : "已确认子会话编号，等待 Windows 登录。")
                        : ex.Message);
                }
            }
            else
            {
                _lastVerifiedChild   = string.Empty;
                _lastTransientStatus = string.Empty;
            }

            // 告知面板当前状态，让它控制 AxHost 可用性和空状态遮罩
            _previewPanel.SetPreviewState(
                prev,
                hasChildSession:        child is not null,
                childSessionsEnabled:   enabled,
                isVerifiedChildSession: verified,
                verificationMessage:    verificationMessage);

            // 音频开关回写（从实际模式同步，不产生循环事件）
            if (_muteAudioToggle != null)
            {
                _muteAudioToggle.Enabled = !_audioReconnect.Active && (disconnected || verified);
                bool shouldMute = _previewPanel.PreviewControl.AudioMode == ChildSessionAudioMode.DoNotPlay;
                if (_muteAudioToggle.Checked != shouldMute)
                {
                    _muteAudioToggle.CheckedChanged -= ChangeAudioMode;
                    _muteAudioToggle.Checked = shouldMute;
                    _muteAudioToggle.CheckedChanged += ChangeAudioMode;
                }
            }

            UpdateHeaderStatus(prev, verified, child);
            bool targetMatches = disconnected || _previewPanel.PreviewControl.VerifiedConnectionTarget == child?.Id;
            UpdateSessionCommands(!disconnected || _audioReconnect.Active, targetMatches && child is { IsChildSession: true, StateQueryError: null } && child.Id != parent.Id);
            SwitchMainView(prev);
            TryPresentStartupUpdate();
            _lastPollError = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or InvalidDataException
                                      or System.Runtime.InteropServices.COMException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException
                                      or UnauthorizedAccessException
                                      or ArgumentException)
        {
            // 瞬态或平台失败：显示给用户但不崩溃刷新循环
            if (_lastPollError != ex.Message) AppendLog($"无法读取会话状态：{ex.Message}", LogLevel.Error);
            _lastPollError = ex.Message;
            if (capability is null) UpdateCapabilityCheck(null);
            UpdateCheckItem(_checkParentIcon, _checkParentText, CheckResult.Error, "状态未确认");
            UpdateCheckItem(_connectionIcon, _connectionText, CheckResult.Error, "状态未确认");
            _previewPanel.SetPreviewUnavailable($"无法读取 Windows 会话状态\r\n{ex.Message}");
            SwitchMainView(RdpPreviewConnectionState.Disconnected);
            Text = $"{Program.DisplayName} — 会话状态不可用";
            if (_connectButton   != null) _connectButton.Enabled   = false;
            UpdateSessionCommands(false, false);
            if (_muteAudioToggle != null) _muteAudioToggle.Enabled = false;
        }
        finally { _refreshInProgress = false; }
    }

    /// <summary>为纸页展示会话编号及中文状态，无法识别的值保留原枚举以便诊断。</summary>
    private static string DescribeSession(SessionSnapshot session)
    {
        if (session.StateQueryError is int error)
            return $"会话 {session.Id.Value} · 状态待确认（{error}）";
        string state = session.ConnectionState switch
        {
            SessionConnectionState.Active => session.IsLocked switch { true => "已锁定", false => "活动中", null => "锁定状态未知" },
            SessionConnectionState.Connected => "已连接",
            SessionConnectionState.Disconnected => "已断开",
            SessionConnectionState.Init or SessionConnectionState.ConnectQuery => "正在准备",
            SessionConnectionState.Idle => "空闲",
            SessionConnectionState.Unknown => "待确认",
            _ => session.ConnectionState.ToString(),
        };
        return $"会话 {session.Id.Value} · {state}";
    }

    /// <summary>同时更新色标和状态文字，使判断结果不只依赖颜色。</summary>
    private static void UpdateCheckItem(Label? icon, Label? text, CheckResult result, string label)
    {
        if (icon == null || text == null) return;
        (icon.Text, icon.ForeColor) = result switch
        {
            CheckResult.Ok      => ("✓", Color.FromArgb(100, 140, 108)),
            CheckResult.Error   => ("✗", Color.FromArgb(150, 80, 72)),
            CheckResult.Warn    => ("◐", Color.FromArgb(192, 144, 72)),
            _                   => ("○", Color.FromArgb(168, 144, 108)),
        };
        text.Text = label;
    }

    private enum CheckResult { Pending, Ok, Warn, Error }

    /// <summary>标题与首页使用同一连接观测值。已有子会话并不代表本窗口已连接到它。</summary>
    internal void UpdateHeaderStatus(RdpPreviewConnectionState state, bool verified, SessionSnapshot? child)
    {
        string status = state switch
        {
            RdpPreviewConnectionState.Connected when verified
                => child is null ? "已连接" : $"已连接 · 会话 {child.Id.Value}",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn && _childNeedsUnlock
                => "等待分身解锁",
            RdpPreviewConnectionState.Connected when _previewPanel.IsPresentingChildSignIn
                => "等待分身登录",
            RdpPreviewConnectionState.Connected
                => "正在核验",
            RdpPreviewConnectionState.Connecting
                => "正在连接",
            _ => "未连接",
        };
        UpdateCheckItem(_connectionIcon, _connectionText,
            state == RdpPreviewConnectionState.Connected ? verified ? CheckResult.Ok : CheckResult.Warn : CheckResult.Pending, status);
        Text = $"{Program.DisplayName} — {status}";
    }

    /// <summary>在连接及目标核验期间显示纸面。面板已开放系统登录或桌面画面时才切换到预览。</summary>
    internal void SwitchMainView(RdpPreviewConnectionState state)
    {
        if (_sessionCommands != null)
            _sessionCommands.Visible = state != RdpPreviewConnectionState.Disconnected || _audioReconnect.Active;
        bool showDesktop = state == RdpPreviewConnectionState.Connected && _previewPanel.PreviewControl.Enabled;

        if (showDesktop && _centerCard?.Visible == true)
        {
            // 先按当前可用空间排好登录画面，再撤下首页，避免露出上次使用的预览尺寸。
            _previewPanel.Dock = DockStyle.Fill;
            _centerCard.Visible = false;
            _previewPanel.BringToFront();
            if (_settingsDrawer != null) _settingsDrawer.Visible = false;
            if (_gear != null) { _gear.Visible = true; _gear.BringToFront(); }
        }
        else if (!showDesktop && _centerCard?.Visible == false)
        {
            if (_settingsDrawer  != null) _settingsDrawer.Visible  = false;
            if (_gear != null) _gear.Visible = false;
            _centerCard.Visible = true;
            _centerCard.BringToFront();
            _previewPanel.Dock = DockStyle.None;
        }
    }

    /// <summary>让侧栏按钮和标题栏菜单使用同一份可操作状态。目标失效时同时禁用两个注销入口。</summary>
    internal void UpdateSessionCommands(bool canDisconnect, bool canLogoff)
    {
        _canDisconnect = canDisconnect;
        _canLogoff = canLogoff;
        if (_disconnectCommand != null) _disconnectCommand.Enabled = canDisconnect;
        if (_logoffCommand != null) _logoffCommand.Enabled = canLogoff;
    }

    private void AppendTransientStatus(string message)
    {
        string s = $"等待子会话核验：{message}";
        if (!string.Equals(_lastTransientStatus, s, StringComparison.Ordinal))
        {
            AppendLog(s, LogLevel.Warning);
            _lastTransientStatus = s;
        }
    }

    internal enum LogLevel { Info, Warning, Error }

    /// <summary>将同一条活动记录写入本次运行的本机日志和预览。磁盘故障在面板内提示，连接操作仍可使用。</summary>
    internal void AppendLog(string message, LogLevel level = LogLevel.Info)
    {
        if (_eventLog == null) return;
        DateTime now = DateTime.Now;
        string line = $"[{now:HH:mm:ss}] {level.ToString().ToUpperInvariant()}  {message}";
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(_logFile, line + Environment.NewLine);
            _logWriteFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_logWriteFailed) AppendLogLine($"[{now:HH:mm:ss}] WARNING  本机日志写入失败：{ex.Message}", LogLevel.Warning);
            _logWriteFailed = true;
        }
        AppendLogLine(line, level);
    }

    /// <summary>将一行带颜色的文字附到末尾。读者正在选择文本时恢复选区，方便继续复制。</summary>
    private void AppendLogLine(string line, LogLevel level)
    {
        if (_eventLog == null) return;
        int start = _eventLog.SelectionStart, length = _eventLog.SelectionLength;
        _eventLog.Select(_eventLog.TextLength, 0);
        _eventLog.SelectionColor = level switch
        {
            LogLevel.Warning => Color.FromArgb(131, 81, 16),
            LogLevel.Error => Color.FromArgb(153, 46, 40),
            _ => Color.FromArgb(43, 86, 86),
        };
        _eventLog.SelectedText = line + "\n";
        if (length > 0) _eventLog.Select(start, length);
        else _eventLog.ScrollToCaret();
    }

    /// <summary>通过系统文件管理器打开当前应用实际写入的日志目录，失败时在日志窗口给出提示。</summary>
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            _openFolder(_logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            AppendLog($"无法打开日志文件夹：{ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>按 Windows 的文件夹打开方式启动资源管理器。</summary>
    private static void OpenFolderInExplorer(string directory)
    {
        using var explorer = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true, Verb = "open" });
    }

    /// <summary>将操作失败记录为错误，并在发起操作的窗口显示原因。</summary>
    private void ShowError(string title, Exception ex)
    {
        AppendLog($"{title}：{ex.Message}", LogLevel.Error);
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool IsDisplayable(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or InvalidDataException
           or COMException or DllNotFoundException or EntryPointNotFoundException
           or UnauthorizedAccessException or ArgumentException;

    // ── 数据记录 ─────────────────────────────────────────────────────────
    private sealed record ResolutionOption(string Label, RemoteDesktopResolution? Resolution)
    {
        public override string ToString() => Label;
    }

    private sealed record ScalingModeOption(string Label, PreviewScalingMode Mode)
    {
        public override string ToString() => Label;
    }
}
