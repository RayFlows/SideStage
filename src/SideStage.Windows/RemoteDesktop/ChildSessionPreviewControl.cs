/*
 * 文件名称：ChildSessionPreviewControl.cs
 * 文件职责：
 *   承载 Windows 自带的 MsRdpClient10 ActiveX，并强制将连接目标限定为本机子会话。
 *
 * 安全与生命周期约束：
 *   - 仅支持 Windows x64、WinForms STA UI 线程和受支持的 MsRdpClient10 ActiveX。
 *   - 连接前必须启用子会话功能；本控件不会擅自修改系统级开关。
 *   - ConnectToChildSession 必须成功写入后才调用 Connect；任何配置异常都直接失败。
 *   - 服务器名固定为 localhost，不接受远端地址，不存在回退主桌面或普通 RDP 的分支。
 *   - DisconnectPreview 只关闭预览连接，不注销子会话；注销操作由 Core 的终止器负责。
 *   - 活动预览切换音频模式时需先断开，再由宿主重新连接；该流程不注销子会话。
 *   - 窗口化预览获得键盘焦点时，Alt+Tab 和 Win 键由 RDP 转发给子会话。
 *   - 可选登录凭据只提交给本机 RDP ActiveX，且在登录结果、断开或错误时请求清除。
 *   - 默认不播放子会话音频；调用方可显式选择转发到本机音频设备。
 *   - ActiveX 的连接是异步的；ConnectPreview 返回仅表示连接请求已发出。
 *
 * COM 互操作说明：
 *   本实现通过 IDispatch 访问系统控件，避免将目标机器生成的互操作程序集复制进仓库。
 *   运行时仍需要 Windows 系统已注册微软 RDP ActiveX 控件；部署前须在目标 Windows 版本实测。
 */

using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;
using SideStage.Windows.Interop;
using SideStage.Windows.RemoteDesktop.Interop;

namespace SideStage.Windows.RemoteDesktop;

/// <summary>
/// 可放入 WinForms 容器的本机子会话 RDP 预览控件。
/// </summary>
public sealed class ChildSessionPreviewControl : AxHost
{
    private static readonly Guid ActiveXClassId = new("8B918B82-7985-4C24-89DF-C33AD2BBFBCD");
    private const string ChildSessionProperty = "ConnectToChildSession";
    private const string LoopbackServerName = "localhost";
    private static readonly Guid RdpEventsId = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private readonly Func<ISessionInspector> _sessionInspectorFactory;
    private ChildSessionAudioMode _audioMode;
    private ChildSessionPreviewSettings _settings;
    private bool _isPrepared;
    private bool _waitingForConnection;
    private bool _rdpPasswordWasSet;
    private SessionId? _connectionTarget;
    private SessionId? _expectedConnectionTarget;

    /// <summary>本次连接已经核对的子会话编号，用于宿主在确认注销时比对目标。新连接会清除旧编号。</summary>
    public SessionId? VerifiedConnectionTarget => _connectionTarget;

    /// <summary>本次连接最后一次 OnLogonError 通知的原始值。该事件也能表示登录进度，零同样是有效通知。</summary>
    public int? LastLogonNotice { get; private set; }

    /// <summary>控件是否已为本次连接报告 OnLoginComplete。桌面可用性仍由 WTS 核验。</summary>
    public bool LoginCompleted { get; private set; }

    /// <summary>本次连接的致命错误码；没有收到对应通知时为空。</summary>
    public int? FatalError { get; private set; }

    /// <summary>本次连接的主动或被动断开原因，按控件的原始编码保留。</summary>
    public int? DisconnectReason { get; private set; }

    /// <summary>
    /// 获取本控件使用的分身音频输出模式。
    /// </summary>
    public ChildSessionAudioMode AudioMode => _audioMode;

    /// <summary>
    /// 获取本控件当前使用的远端分辨率和本地预览显示设置。
    /// </summary>
    public ChildSessionPreviewSettings Settings => _settings;

    /// <summary>
    /// 更新远端分辨率或本地预览显示策略。
    /// </summary>
    /// <param name="settings">通过构造函数完成校验的完整预览设置。</param>
    /// <exception cref="ArgumentNullException">设置对象为空时抛出。</exception>
    /// <exception cref="InvalidOperationException">
    /// 连接正在建立时，或连接中请求改变远端分辨率时抛出。
    /// </exception>
    /// <remarks>
    /// 已连接时允许只修改 SmartSizing 显示模式；远端桌面宽高由 RDP 在连接时协商，
    /// 因此要更改宽高必须先断开预览。设置不会注销子会话。
    /// </remarks>
    public void SetSettings(ChildSessionPreviewSettings settings)
    {
        EnsureUiThread();
        ArgumentNullException.ThrowIfNull(settings);

        RdpPreviewConnectionState connectionState = GetConnectionState();
        if (connectionState == RdpPreviewConnectionState.Connecting)
        {
            throw new InvalidOperationException("RDP 连接建立期间不能修改预览设置。");
        }

        if (connectionState == RdpPreviewConnectionState.Connected
            && settings.Resolution != _settings.Resolution)
        {
            throw new InvalidOperationException("远端分辨率只在建立连接时生效；请先断开预览，再更改分辨率。");
        }

        if (_settings == settings)
        {
            return;
        }

        if (_isPrepared)
        {
            object activeX = GetActiveXOrThrow();
            if (connectionState == RdpPreviewConnectionState.Connected)
            {
                ConfigurePreviewScaling(activeX, settings.ScalingMode);
            }
            else
            {
                ConfigureDisplaySettings(activeX, settings);
            }
        }

        _settings = settings;
    }

    /// <summary>
    /// 设置下一次连接使用的音频输出模式。
    /// </summary>
    /// <param name="audioMode">不播放分身声音，或将其转发到本机。</param>
    /// <exception cref="ArgumentOutOfRangeException">指定了未支持的音频模式。</exception>
    /// <exception cref="InvalidOperationException">预览当前正在连接或已连接时抛出。</exception>
    public void SetAudioMode(ChildSessionAudioMode audioMode)
    {
        EnsureUiThread();
        ValidateAudioMode(audioMode);

        if (GetConnectionState() != RdpPreviewConnectionState.Disconnected)
        {
            throw new InvalidOperationException("请先断开预览，再更改分身音频模式。");
        }

        if (_audioMode == audioMode)
        {
            return;
        }

        if (_isPrepared)
        {
            ConfigureAudioMode(GetActiveXOrThrow(), audioMode);
        }

        _audioMode = audioMode;
    }

    /// <summary>
    /// 修改分身音频模式；若预览正在连接或已经连接，则先断开当前预览，
    /// 由调用方在控件变为 Disconnected 后重新发起连接。
    /// </summary>
    /// <param name="audioMode">不播放分身声音，或将其转发到本机。</param>
    /// <returns>当前预览需要重新连接时返回 true。</returns>
    /// <exception cref="ArgumentOutOfRangeException">指定了未支持的音频模式。</exception>
    /// <exception cref="InvalidOperationException">无法读取或断开当前预览连接时抛出。</exception>
    public bool RequestAudioModeChange(ChildSessionAudioMode audioMode)
    {
        EnsureUiThread();
        ValidateAudioMode(audioMode);

        if (_audioMode == audioMode)
        {
            return false;
        }

        if (GetConnectionState() == RdpPreviewConnectionState.Disconnected)
        {
            SetAudioMode(audioMode);
            return false;
        }

        // RDP 音频重定向只能在下一次连接协商时应用，因此先保留目标模式，再断开旧连接。
        DisconnectPreview();
        _audioMode = audioMode;
        return true;
    }

    /// <summary>
    /// 建立新分身，或仅重连指定的原分身。需要在运行 WinForms 消息循环的宿主线程调用。
    /// 已连接时只核验，不发起重复连接；超时、取消及身份变更只断开本次发起的预览。
    /// </summary>
    /// <param name="expected">新建传空；重连须传入之前保存的子会话编号。</param>
    /// <param name="cancellationToken">取消握手，不注销任何会话。</param>
    /// <param name="credentials">可选的本次 Windows 登录凭据；不会由控件持久化。</param>
    /// <returns>已由 WTS 确认的活动子会话。</returns>
    public async Task<SessionSnapshot> ConnectAndVerifyAsync(
        SessionId? expected = null,
        CancellationToken cancellationToken = default,
        WindowsChildSessionCredentials? credentials = null)
    {
        EnsureUiThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Application.MessageLoop || _waitingForConnection)
        {
            throw new InvalidOperationException("需要运行中的 UI 消息循环，且不能重复建立连接。");
        }

        ISessionInspector inspector = CreateSessionInspector();
        if (expected.HasValue && inspector.TryGetChildSession()?.Id != expected)
        {
            throw new InvalidOperationException("原子会话已结束或被替换，拒绝建立新的替代会话。");
        }

        bool initiated = false;
        SessionSnapshot? lastObservedChild = null;
        _waitingForConnection = true;
        try
        {
            if (GetConnectionState() == RdpPreviewConnectionState.Disconnected)
            {
                StartPreviewConnection(credentials, expected);
                initiated = true;
            }

            long start = Environment.TickCount64;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                RdpPreviewConnectionState state = GetConnectionState();
                lastObservedChild = inspector.TryGetChildSession();
                if (expected.HasValue && lastObservedChild is not null && lastObservedChild.Id != expected)
                {
                    throw new InvalidOperationException($"期望重连 {expected}，Windows 为连接分配了 {lastObservedChild.Id}。");
                }
                if (state == RdpPreviewConnectionState.Connected && lastObservedChild?.IsDesktopReady == true)
                {
                    SessionSnapshot child = VerifyConnectedChildSession();
                    if (expected.HasValue && child.Id != expected)
                    {
                        throw new InvalidOperationException("连接期间子会话身份发生变化。");
                    }

                    return child;
                }

                if (state == RdpPreviewConnectionState.Disconnected && Environment.TickCount64 - start > 500)
                {
                    object? reason = GetComProperty(GetActiveXOrThrow(), "ExtendedDisconnectReason");
                    throw new InvalidOperationException($"子会话连接已断开。原因 {DisconnectReason}，扩展原因 {reason}，致命错误 {FatalError}。");
                }

                if (Environment.TickCount64 - start > 30000)
                {
                    string stateQuery = lastObservedChild?.StateQueryError is int error
                        ? $"，WTS 状态查询错误：{error}"
                        : string.Empty;
                    throw new TimeoutException(
                        $"等待活动子会话超过 30 秒，预览状态：{state}，目标会话：{lastObservedChild?.Id.ToString() ?? "未分配"}{stateQuery}。" +
                        $"桌面锁定：{lastObservedChild?.IsLocked?.ToString() ?? "未知"}，RDP 登录完成通知：{LoginCompleted}，登录通知码：{LastLogonNotice?.ToString() ?? "未收到"}。");
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch
        {
            if (initiated && !IsDisposed && GetConnectionState() != RdpPreviewConnectionState.Disconnected)
            {
                DisconnectPreview();
            }

            throw;
        }
        finally
        {
            _waitingForConnection = false;
        }
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件的 WinForms 容器。
    /// </summary>
    public ChildSessionPreviewControl()
        : this(
            static () => new WindowsSessionInspector(),
            ChildSessionAudioMode.DoNotPlay,
            ChildSessionPreviewSettings.Default)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件，并注入会话观察器工厂。
    /// </summary>
    /// <param name="sessionInspectorFactory">创建会话观察器的工厂。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sessionInspectorFactory"/> 为 null 时抛出。
    /// </exception>
    public ChildSessionPreviewControl(Func<ISessionInspector> sessionInspectorFactory)
        : this(sessionInspectorFactory, ChildSessionAudioMode.DoNotPlay, ChildSessionPreviewSettings.Default)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件，并指定子会话音频输出策略。
    /// </summary>
    /// <param name="audioMode">本连接的分身音频输出模式。</param>
    public ChildSessionPreviewControl(ChildSessionAudioMode audioMode)
        : this(static () => new WindowsSessionInspector(), audioMode, ChildSessionPreviewSettings.Default)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件并指定初始预览显示设置。
    /// </summary>
    /// <param name="settings">远端分辨率与本地显示方式。</param>
    /// <param name="audioMode">本连接的分身音频输出策略。</param>
    /// <exception cref="ArgumentNullException">设置对象为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">音频模式不受支持时抛出。</exception>
    public ChildSessionPreviewControl(
        ChildSessionPreviewSettings settings,
        ChildSessionAudioMode audioMode = ChildSessionAudioMode.DoNotPlay)
        : this(static () => new WindowsSessionInspector(), audioMode, settings)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件，注入会话观察器工厂并指定音频输出策略。
    /// </summary>
    /// <param name="sessionInspectorFactory">创建会话观察器的工厂。</param>
    /// <param name="audioMode">本连接的分身音频输出模式。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sessionInspectorFactory"/> 为 null 时抛出。
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="audioMode"/> 不是受支持的模式时抛出。
    /// </exception>
    public ChildSessionPreviewControl(
        Func<ISessionInspector> sessionInspectorFactory,
        ChildSessionAudioMode audioMode)
        : this(sessionInspectorFactory, audioMode, ChildSessionPreviewSettings.Default)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件，注入会话观察器工厂并指定初始预览设置。
    /// </summary>
    /// <param name="sessionInspectorFactory">创建会话观察器的工厂。</param>
    /// <param name="settings">远端分辨率与本地显示方式。</param>
    /// <param name="audioMode">本连接的分身音频输出策略。</param>
    /// <exception cref="ArgumentNullException">观察器工厂或设置对象为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">音频模式不受支持时抛出。</exception>
    public ChildSessionPreviewControl(
        Func<ISessionInspector> sessionInspectorFactory,
        ChildSessionPreviewSettings settings,
        ChildSessionAudioMode audioMode = ChildSessionAudioMode.DoNotPlay)
        : this(sessionInspectorFactory, audioMode, settings)
    {
    }

    /// <summary>
    /// 创建系统 RDP ActiveX 控件，注入会话观察器工厂并指定音频和显示设置。
    /// </summary>
    /// <param name="sessionInspectorFactory">创建会话观察器的工厂。</param>
    /// <param name="audioMode">本连接的分身音频输出策略。</param>
    /// <param name="settings">远端分辨率与本地显示方式。</param>
    /// <exception cref="ArgumentNullException">观察器工厂或设置对象为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">音频模式不受支持时抛出。</exception>
    public ChildSessionPreviewControl(
        Func<ISessionInspector> sessionInspectorFactory,
        ChildSessionAudioMode audioMode,
        ChildSessionPreviewSettings settings)
        : base(ActiveXClassId.ToString("B"))
    {
        ValidateAudioMode(audioMode);
        _sessionInspectorFactory = sessionInspectorFactory
            ?? throw new ArgumentNullException(nameof(sessionInspectorFactory));
        _settings = settings
            ?? throw new ArgumentNullException(nameof(settings));
        _audioMode = audioMode;

        SetStyle(ControlStyles.UserPaint, false);
        Dock = DockStyle.Fill;
        TabStop = true;
    }

    /// <summary>
    /// 获取当前控件的连接状态。
    /// </summary>
    /// <returns>根据 Windows RDP ActiveX Connected 属性映射的状态。</returns>
    /// <exception cref="InvalidOperationException">
    /// 控件不是在 STA UI 线程使用，或 ActiveX 返回了未知状态时抛出。
    /// </exception>
    public RdpPreviewConnectionState GetConnectionState()
    {
        EnsureUiThread();

        if (!IsHandleCreated)
        {
            return RdpPreviewConnectionState.Disconnected;
        }

        object activeX = GetActiveXOrThrow();
        object? nativeState = GetComProperty(activeX, "Connected");
        int state = Convert.ToInt32(nativeState, System.Globalization.CultureInfo.InvariantCulture);

        return state switch
        {
            0 => RdpPreviewConnectionState.Disconnected,
            1 => RdpPreviewConnectionState.Connected,
            2 => RdpPreviewConnectionState.Connecting,
            _ => throw new InvalidOperationException(
                $"RDP ActiveX 返回了未知连接状态 {state}。"),
        };
    }

    /// <summary>
    /// 创建并配置 RDP ActiveX，但不建立任何 Windows 会话连接。
    /// </summary>
    /// <remarks>
    /// 可在正式连接前调用，用于验证当前 Windows 是否注册了兼容的 ActiveX，
    /// 以及控件是否接受所选分辨率、缩放方式和子会话连接属性。
    /// 重复调用是安全的；该方法不会启用系统子会话，也不会创建或注销会话。
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// ActiveX 配置失败或调用线程不是 STA UI 线程时抛出。
    /// </exception>
    public void PreparePreview()
    {
        EnsureUiThread();
        CreateControl();

        if (_isPrepared)
        {
            return;
        }

        try
        {
            ConfigureChildSessionTarget(GetActiveXOrThrow(), _audioMode, _settings);
            _isPrepared = true;
        }
        catch (Exception ex) when (ex is TargetInvocationException
            or COMException
            or MissingMethodException
            or InvalidCastException
            or ArgumentException)
        {
            throw new InvalidOperationException(
                "无法将 RDP ActiveX 配置为固定分辨率的本机子会话预览。",
                UnwrapComException(ex));
        }
    }

    /// <summary>
    /// 订阅系统类型库 IMsTscAxEvents 中与登录有关的通知。仅保存本次连接的错误码，
    /// 不读取用户名或凭据；登录通知和桌面可用性分别判断。
    /// </summary>
    protected override void CreateSink()
    {
        base.CreateSink();
        object control = GetActiveXOrThrow();
        ComEventsHelper.Combine(control, RdpEventsId, 3, (Action)OnLoginComplete);
        ComEventsHelper.Combine(control, RdpEventsId, 4, (Action<int>)OnRemoteDisconnect);
        ComEventsHelper.Combine(control, RdpEventsId, 10, (Action<int>)OnFatalError);
        ComEventsHelper.Combine(control, RdpEventsId, 22, (Action<int>)OnLogonNotice);
    }

    /// <summary>
    /// 解除控件连接点上的委托，避免关闭预览以后 COM 仍然持有旧窗口。
    /// </summary>
    protected override void DetachSink()
    {
        TryClearRdpPassword();
        if (GetOcx() is { } control)
        {
            ComEventsHelper.Remove(control, RdpEventsId, 3, (Action)OnLoginComplete);
            ComEventsHelper.Remove(control, RdpEventsId, 4, (Action<int>)OnRemoteDisconnect);
            ComEventsHelper.Remove(control, RdpEventsId, 10, (Action<int>)OnFatalError);
            ComEventsHelper.Remove(control, RdpEventsId, 22, (Action<int>)OnLogonNotice);
        }

        _isPrepared = false;
        base.DetachSink();
    }

    /// <summary>
    /// 记录当前连接收到的登录完成通知，并清除 RDP ActiveX 暂存的密码。
    /// </summary>
    private void OnLoginComplete()
    {
        LoginCompleted = true;
        TryClearRdpPassword();
    }

    /// <summary>
    /// 记录连接断开的原始原因，并清除 RDP ActiveX 暂存的密码。
    /// </summary>
    /// <param name="reason">微软 RDP 控件返回的断开原因码。</param>
    private void OnRemoteDisconnect(int reason)
    {
        DisconnectReason = reason;
        TryClearRdpPassword();
    }

    /// <summary>
    /// 记录系统控件报告的致命错误，并清除 RDP ActiveX 暂存的密码。
    /// </summary>
    /// <param name="error">OnFatalError 原始数值。</param>
    private void OnFatalError(int error)
    {
        FatalError = error;
        TryClearRdpPassword();
    }

    /// <summary>
    /// 保留登录提示或错误的原始数值；非零错误时清除自动提交密码，保留人工登录界面。
    /// </summary>
    /// <param name="notice">OnLogonError 原始数值，命名空间与 Win32 错误码不同。</param>
    private void OnLogonNotice(int notice)
    {
        LastLogonNotice = notice;
        if (notice != 0)
        {
            TryClearRdpPassword();
        }
    }

    /// <summary>
    /// 发起连接到本机子会话的异步预览请求。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 控件状态不允许连接、子会话未启用，或连接目标配置未能确认时抛出。
    /// </exception>
    /// <exception cref="COMException">
    /// RDP ActiveX 无法配置或拒绝连接请求时抛出。
    /// </exception>
    /// <remarks>
    /// 返回后请通过 <see cref="GetConnectionState"/> 和
    /// <see cref="WindowsSessionInspector.TryGetChildSession"/> 轮询最终状态。
    /// ActiveX 的 Connect 方法按 Windows 定义异步启动连接。
    /// </remarks>
    public void ConnectPreview(WindowsChildSessionCredentials? credentials = null) =>
        StartPreviewConnection(credentials, null);

    /// <summary>异步重连已知分身。提交连接前复核编号，并在登录期间持续按该编号验证目标。</summary>
    public void ReconnectPreview(SessionId expected, WindowsChildSessionCredentials? credentials = null) =>
        StartPreviewConnection(credentials, expected);

    /// <summary>将本次连接绑定到预期分身；普通新连接则在首次核验时绑定。凭据只在本次调用中使用。</summary>
    private void StartPreviewConnection(WindowsChildSessionCredentials? credentials, SessionId? expected)
    {
        EnsureUiThread();

        if (GetConnectionState() != RdpPreviewConnectionState.Disconnected)
        {
            throw new InvalidOperationException("只有已断开的预览控件才能发起连接。");
        }

        ISessionInspector inspector = CreateSessionInspector();
        if (expected.HasValue && inspector.TryGetChildSession()?.Id != expected)
            throw new InvalidOperationException("原子会话已结束或被替换，已取消重连。");
        if (!inspector.IsChildSessionsEnabled())
        {
            throw new InvalidOperationException(
                "Windows 子会话功能尚未启用。请通过独立的系统设置流程显式启用后重试。");
        }

        PreparePreview();

        try
        {
            // 控件可能已准备过；每次连接前重写模式，确保运行时切换在重连后生效。
            ConfigureAudioMode(GetActiveXOrThrow(), _audioMode);
            ConfigureDisplaySettings(GetActiveXOrThrow(), _settings);
            _expectedConnectionTarget = expected;
            _connectionTarget = null;
            LastLogonNotice = FatalError = DisconnectReason = null;
            LoginCompleted = false;
            if (credentials is not null)
            {
                ConfigureLogonCredentials(GetActiveXOrThrow(), credentials);
            }

            InvokeComMethod(GetActiveXOrThrow(), "Connect");
        }
        catch (Exception ex) when (ex is TargetInvocationException
            or COMException
            or MissingMethodException
            or InvalidCastException
            or ArgumentException)
        {
            TryClearRdpPassword();
            // 连接属性未能设置或 COM 调用失败时立即中止，不允许继续为普通服务器或父桌面建立连接。
            throw new InvalidOperationException(
                "无法将 RDP ActiveX 安全配置为本机子会话连接；连接请求已中止。",
                UnwrapComException(ex));
        }
    }

    /// <summary>
    /// 判断系统控件能否显示子会话的原生登录或桌面准备画面。仅对已读回本机子会话配置的
    /// 连接开放，并把当前连接绑定到首次确认的独立子会话编号。此判断仅用于人工交互。
    /// </summary>
    /// <returns>可在预览内交由用户处理 Windows 登录时返回 true。</returns>
    public bool CanPresentChildSignIn()
    {
        if (!_isPrepared || GetConnectionState() != RdpPreviewConnectionState.Connected)
        {
            return false;
        }

        ISessionInspector inspector = CreateSessionInspector();
        SessionSnapshot? child = inspector.TryGetChildSession();
        if (child is not { IsChildSession: true } || child.Id == inspector.GetCurrentSession().Id ||
            (_expectedConnectionTarget.HasValue && child.Id != _expectedConnectionTarget))
        {
            return false;
        }

        // 登录桌面期间 WTS 可拒绝查询状态，但仍能返回父子关系。已经消失的旧编号不能放行。
        if (!child.CanPresentSignIn)
        {
            return false;
        }

        _connectionTarget ??= child.Id;
        return _connectionTarget == child.Id;
    }

    /// <summary>
    /// 验证预览已连接到当前父会话的活动子会话，并返回其状态快照。
    /// </summary>
    /// <returns>已核验的子会话状态快照。</returns>
    /// <exception cref="InvalidOperationException">
    /// RDP 控件尚未连接、没有子会话，或子会话身份/状态与预期不符时抛出。
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 原生会话查询失败时抛出。
    /// </exception>
    /// <remarks>
    /// 由于 ActiveX Connect 是异步的，调用方应在连接状态变为 Connected 后调用本方法。
    /// 仅看到控件连接成功，不足以证明目标 Windows 子会话已处于活动状态。
    /// </remarks>
    public SessionSnapshot VerifyConnectedChildSession()
    {
        EnsureUiThread();

        if (GetConnectionState() != RdpPreviewConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "RDP 预览控件尚未进入 Connected 状态，不能确认子会话身份。");
        }

        ISessionInspector inspector = CreateSessionInspector();
        SessionSnapshot parentSession = inspector.GetCurrentSession();
        SessionSnapshot? childSession = inspector.TryGetChildSession();

        if (childSession is null)
        {
            throw new InvalidOperationException(
                "RDP 控件已连接，但 Windows 未报告当前父会话存在子会话。");
        }

        if (!childSession.IsChildSession || childSession.Id == parentSession.Id
            || (_expectedConnectionTarget.HasValue && childSession.Id != _expectedConnectionTarget)
            || (_connectionTarget.HasValue && childSession.Id != _connectionTarget))
        {
            throw new InvalidOperationException(
                $"RDP 控件连接目标无法确认为独立子会话（父会话：{parentSession.Id}，目标：{childSession.Id}）。");
        }

        if (childSession.ConnectionState != SessionConnectionState.Active)
        {
            throw new InvalidOperationException(
                $"子会话 {childSession.Id} 当前状态为 {childSession.ConnectionState}，不是活动状态。");
        }

        if (!childSession.IsDesktopReady)
        {
            throw new InvalidOperationException(childSession.IsLocked == true
                ? $"分身 {childSession.Id} 已锁定，请在预览内解锁。"
                : $"分身 {childSession.Id} 的解锁状态未知。");
        }

        _connectionTarget = childSession.Id;
        return childSession;
    }

    /// <summary>
    /// 创建并校验注入的会话观察器，确保依赖工厂没有返回空实例。
    /// </summary>
    /// <returns>可用于本次操作的会话观察器。</returns>
    /// <exception cref="InvalidOperationException">
    /// 注入的工厂返回 null 时抛出。
    /// </exception>
    private ISessionInspector CreateSessionInspector()
    {
        return _sessionInspectorFactory()
            ?? throw new InvalidOperationException(
                "会话观察器工厂返回了 null，已中止连接操作。");
    }

    /// <summary>
    /// 断开 RDP 预览连接，但保留子会话及其内部进程。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 控件状态不是已连接或正在连接时抛出。
    /// </exception>
    /// <exception cref="COMException">
    /// RDP ActiveX 拒绝断开请求时抛出。
    /// </exception>
    /// <remarks>
    /// 关闭预览与注销子会话是两个独立操作。该方法只调用 ActiveX Disconnect，
    /// 不调用 WTSLogoffSession。
    /// </remarks>
    public void DisconnectPreview()
    {
        EnsureUiThread();

        RdpPreviewConnectionState state = GetConnectionState();
        if (state == RdpPreviewConnectionState.Disconnected)
        {
            throw new InvalidOperationException("预览控件当前未连接。");
        }

        try
        {
            InvokeComMethod(GetActiveXOrThrow(), "Disconnect");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException error)
        {
            throw new InvalidOperationException($"RDP 拒绝断开预览（0x{error.HResult:X8}）。", error);
        }
        TryClearRdpPassword();
    }

    /// <summary>
    /// 验证当前调用发生在创建 ActiveX 控件的 STA UI 线程。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 当前线程不是 STA，或调用来自另一个 UI 线程时抛出。
    /// </exception>
    private void EnsureUiThread()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "RDP ActiveX 控件必须在 STA UI 线程中调用。");
        }

        if (IsHandleCreated && InvokeRequired)
        {
            throw new InvalidOperationException(
                "RDP ActiveX 控件只能由创建该控件的 UI 线程调用。");
        }
    }

    /// <summary>
    /// 获取已经创建的 ActiveX 实例，避免在 COM 控件未初始化时继续调用反射。
    /// </summary>
    /// <returns>已创建且非空的 RDP ActiveX 对象。</returns>
    /// <exception cref="InvalidOperationException">
    /// ActiveX 控件尚未创建时抛出。
    /// </exception>
    private object GetActiveXOrThrow()
    {
        return GetOcx()
            ?? throw new InvalidOperationException(
                "RDP ActiveX 尚未创建，不能读取或调用其 COM 接口。");
    }

    /// <summary>
    /// 将 RDP 控件设置为只连接本机子会话，并请求固定的逻辑桌面分辨率。
    /// </summary>
    /// <param name="activeX">由 AxHost 创建的 RDP COM 控件。</param>
    /// <param name="audioMode">本连接的分身音频输出模式。</param>
    /// <param name="settings">本连接请求的远端尺寸与本地缩放模式。</param>
    private static void ConfigureChildSessionTarget(
        object activeX,
        ChildSessionAudioMode audioMode,
        ChildSessionPreviewSettings settings)
    {
        // ActiveX 只允许从本机回环目标建立连接；不暴露任何远程主机配置入口。
        SetComProperty(activeX, "Server", LoopbackServerName);

        // ExtendedSettings 是独立的 COM 接口，不能作为 ActiveX 的普通自动化属性读取。
        IMsRdpExtendedSettings extendedSettings = (IMsRdpExtendedSettings)activeX;
        object childSessionValueToSet = true;
        extendedSettings.SetProperty(ChildSessionProperty, ref childSessionValueToSet);
        extendedSettings.GetProperty(ChildSessionProperty, out object childSessionValue);

        object? advancedSettings = GetComProperty(activeX, "AdvancedSettings9")
            ?? throw new InvalidOperationException("RDP ActiveX 没有提供 AdvancedSettings9 接口。");

        // 允许系统协商回环认证；Connect 成功后仍需等待用户登录及 WTS 活动状态。
        SetComProperty(advancedSettings, "EnableCredSspSupport", true);

        // 宿主最大化时 ActiveX 仍处于窗口模式。连接前指定快捷键目标，让系统控件随焦点处理转发。
        const int applyShortcutsToRemote = 1;
        object securedSettings = GetComProperty(activeX, "SecuredSettings2")
            ?? throw new InvalidOperationException("RDP ActiveX 没有提供 SecuredSettings2 接口。");
        SetComProperty(securedSettings, "KeyboardHookMode", applyShortcutsToRemote);
        SetComProperty(advancedSettings, "EnableWindowsKey", 1);
        VerifyIntegerProperty(securedSettings, "KeyboardHookMode", applyShortcutsToRemote);
        VerifyBooleanProperty(advancedSettings, "EnableWindowsKey", expectedValue: true);

        ConfigureDisplaySettings(activeX, settings);

        // 除非调用方明确要求播放，否则不把后台分身声音转发到主桌面。
        ConfigureAudioMode(activeX, audioMode);

        // 写入 COM 属性后立即读回；不接受“设置调用没报错，但实际目标或尺寸未生效”的状态。
        VerifyStringProperty(activeX, "Server", LoopbackServerName);
        VerifyBooleanProperty(advancedSettings, "EnableCredSspSupport", expectedValue: true);
        if (!Convert.ToBoolean(childSessionValue, System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException(
                $"RDP ActiveX 未能确认扩展属性 {ChildSessionProperty} 已启用。");
        }
    }

    /// <summary>
    /// 配置并读回 RDP 远端分辨率与本地 SmartSizing 模式。
    /// </summary>
    /// <param name="activeX">已经创建的 RDP ActiveX 控件。</param>
    /// <param name="settings">需要应用的预览设置。</param>
    private static void ConfigureDisplaySettings(
        object activeX,
        ChildSessionPreviewSettings settings)
    {
        SetComProperty(activeX, "DesktopWidth", settings.Resolution.Width);
        SetComProperty(activeX, "DesktopHeight", settings.Resolution.Height);
        ConfigurePreviewScaling(activeX, settings.ScalingMode);

        // 写入后读回，避免将未被 ActiveX 接受的分辨率当成已应用设置。
        VerifyIntegerProperty(activeX, "DesktopWidth", settings.Resolution.Width);
        VerifyIntegerProperty(activeX, "DesktopHeight", settings.Resolution.Height);
    }

    /// <summary>
    /// 配置并读回 RDP ActiveX 的本地预览缩放策略。
    /// </summary>
    /// <param name="activeX">已经创建的 RDP ActiveX 控件。</param>
    /// <param name="scalingMode">1:1 原像素显示或自适应显示。</param>
    private static void ConfigurePreviewScaling(object activeX, PreviewScalingMode scalingMode)
    {
        object advancedSettings = GetComProperty(activeX, "AdvancedSettings9")
            ?? throw new InvalidOperationException("RDP ActiveX 没有提供 AdvancedSettings9 接口。");
        bool smartSizing = scalingMode == PreviewScalingMode.Adaptive;
        SetComProperty(advancedSettings, "SmartSizing", smartSizing);
        VerifyBooleanProperty(advancedSettings, "SmartSizing", smartSizing);
    }

    /// <summary>
    /// 在 RDP ActiveX 上配置并读回音频重定向模式。
    /// </summary>
    /// <param name="activeX">已创建的 RDP ActiveX 控件。</param>
    /// <param name="audioMode">目标音频输出模式。</param>
    private static void ConfigureAudioMode(object activeX, ChildSessionAudioMode audioMode)
    {
        object advancedSettings = GetComProperty(activeX, "AdvancedSettings9")
            ?? throw new InvalidOperationException("RDP ActiveX 没有提供 AdvancedSettings9 接口。");
        SetComProperty(advancedSettings, "AudioRedirectionMode", (int)audioMode);
        VerifyIntegerProperty(advancedSettings, "AudioRedirectionMode", (int)audioMode);
    }

    /// <summary>
    /// 拒绝未定义的音频策略，避免将未知数值传入 RDP ActiveX。
    /// </summary>
    /// <param name="audioMode">需要检查的音频输出模式。</param>
    /// <exception cref="ArgumentOutOfRangeException">音频模式不受支持。</exception>
    private static void ValidateAudioMode(ChildSessionAudioMode audioMode)
    {
        if (audioMode is not ChildSessionAudioMode.DoNotPlay
            and not ChildSessionAudioMode.RedirectToThisComputer)
        {
            throw new ArgumentOutOfRangeException(nameof(audioMode), audioMode, "不支持指定的分身音频输出模式。");
        }
    }

    /// <summary>
    /// 将本次连接的账户、域和密码交给本机 RDP ActiveX，不写入应用配置。
    /// </summary>
    /// <param name="activeX">已经配置为本机子会话目标的 RDP 控件。</param>
    /// <param name="credentials">本次连接使用的受保护凭据。</param>
    /// <exception cref="InvalidOperationException">ActiveX 不支持必要的凭据接口时抛出。</exception>
    private void ConfigureLogonCredentials(object activeX, WindowsChildSessionCredentials credentials)
    {
        SetComProperty(activeX, "UserName", credentials.UserName);
        SetComProperty(activeX, "Domain", credentials.Domain);

        // 用户名不是秘密；读回可确认 ActiveX 接受了这次登录目标。
        VerifyStringProperty(activeX, "UserName", credentials.UserName);
        VerifyStringProperty(activeX, "Domain", credentials.Domain);

        using System.Security.SecureString password = credentials.CopyPassword();
        IntPtr passwordBstr = Marshal.SecureStringToBSTR(password);
        try
        {
            IMsRdpClientNonScriptable nonScriptable = (IMsRdpClientNonScriptable)activeX;

            // 标记必须先于 COM 调用，以便即使调用抛错也尝试清理 ActiveX 内部状态。
            _rdpPasswordWasSet = true;
            nonScriptable.PutClearTextPassword(passwordBstr);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(passwordBstr);
        }
    }

    /// <summary>
    /// 尽力清除 RDP ActiveX 内部保存的密码；事件回调中不传播 COM 清理异常。
    /// </summary>
    private void TryClearRdpPassword()
    {
        if (!_rdpPasswordWasSet || GetOcx() is not { } activeX)
        {
            return;
        }

        try
        {
            ((IMsRdpClientNonScriptable)activeX).ResetPassword();
            _rdpPasswordWasSet = false;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            // 连接收尾期间 COM 可能已经销毁；后续断开与控件释放仍会再次尝试清理。
        }
    }

    /// <summary>
    /// 读取并核对 COM 文本属性。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="propertyName">COM 属性名称。</param>
    /// <param name="expectedValue">预期文本值。</param>
    private static void VerifyStringProperty(object target, string propertyName, string expectedValue)
    {
        string? actualValue = Convert.ToString(
            GetComProperty(target, propertyName),
            System.Globalization.CultureInfo.InvariantCulture);

        if (!string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"RDP ActiveX 属性 {propertyName} 读回值为“{actualValue}”，预期为“{expectedValue}”。");
        }
    }

    /// <summary>
    /// 读取并核对 COM 整数属性。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="propertyName">COM 属性名称。</param>
    /// <param name="expectedValue">预期整数值。</param>
    private static void VerifyIntegerProperty(object target, string propertyName, int expectedValue)
    {
        int actualValue = Convert.ToInt32(
            GetComProperty(target, propertyName),
            System.Globalization.CultureInfo.InvariantCulture);

        if (actualValue != expectedValue)
        {
            throw new InvalidOperationException(
                $"RDP ActiveX 属性 {propertyName} 读回值为 {actualValue}，预期为 {expectedValue}。");
        }
    }

    /// <summary>
    /// 读取并核对 COM 布尔属性。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="propertyName">COM 属性名称。</param>
    /// <param name="expectedValue">预期布尔值。</param>
    private static void VerifyBooleanProperty(object target, string propertyName, bool expectedValue)
    {
        bool actualValue = Convert.ToBoolean(
            GetComProperty(target, propertyName),
            System.Globalization.CultureInfo.InvariantCulture);

        if (actualValue != expectedValue)
        {
            throw new InvalidOperationException(
                $"RDP ActiveX 属性 {propertyName} 读回值为 {actualValue}，预期为 {expectedValue}。");
        }
    }

    /// <summary>
    /// 通过 COM IDispatch 读取一个无参数属性。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <returns>COM 属性值。</returns>
    private static object? GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            args: null,
            modifiers: null,
            culture: System.Globalization.CultureInfo.InvariantCulture,
            namedParameters: null);
    }

    /// <summary>
    /// 通过 COM IDispatch 设置一个无参数属性。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <param name="value">需要写入的属性值。</param>
    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            args: [value],
            modifiers: null,
            culture: System.Globalization.CultureInfo.InvariantCulture,
            namedParameters: null);
    }

    /// <summary>
    /// 通过 COM IDispatch 调用一个无参数方法。
    /// </summary>
    /// <param name="target">目标 COM 对象。</param>
    /// <param name="methodName">方法名称。</param>
    private static void InvokeComMethod(object target, string methodName)
    {
        target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            args: null,
            modifiers: null,
            culture: System.Globalization.CultureInfo.InvariantCulture,
            namedParameters: null);
    }

    /// <summary>
    /// 解开反射调用包装异常，保留最内层 COM 错误诊断。
    /// </summary>
    /// <param name="exception">反射或 COM 调用抛出的异常。</param>
    /// <returns>最内层 COM/反射异常。</returns>
    private static Exception UnwrapComException(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException { InnerException: Exception innerException })
        {
            current = innerException;
        }

        return current;
    }
}
