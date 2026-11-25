/*
 * 文件名称：WindowsTaskSchedulerChildSessionProcessStarter.cs
 * 文件职责：
 *   使用 Windows Task Scheduler 2.0 COM 接口，将一个会话内启动代理运行到
 *   明确指定的交互式子会话，并通过受限命名管道取得代理实际创建的目标 PID。
 *
 * 实现策略：
 *   - 启动前创建一次性命名管道和随机认证令牌。
 *   - Task Scheduler 只启动 SideStage.SessionAgent；真正的目标程序由代理在
 *     自己所在的子会话内启动，避免把父会话的 Process.Start 当成跨会话启动。
 *   - 代理通过协议返回 PID、真实 SessionId 和错误阶段；宿主在返回前再次核验
 *     PID 的会话与完整路径，并发送接管确认。
 *   - 代理没有收到确认时会终止目标进程，避免错误进程残留。
 *
 * 清理约束：
 *   - 每次调用创建随机任务名，不复用、不修改用户已有任务。
 *   - 目标进程握手完成后立即删除临时任务；任务删除不会终止已接管的目标进程。
 *   - 任何校验失败都不尝试普通 Process.Start，不允许回退到父桌面。
 *
 * 部署要求：
 *   - SideStage.SessionAgent.exe 必须随 Windows 程序集部署到同一目录，或通过
 *     SIDESTAGE_SESSION_AGENT_PATH 环境变量/显式构造参数提供绝对路径。
 *   - 代理只使用当前用户权限，不保存密码、令牌或业务配置。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Windows.Processes;

/// <summary>
/// 通过临时 Task Scheduler 任务和会话内代理启动子会话进程。
/// </summary>
internal sealed class WindowsTaskSchedulerChildSessionProcessStarter
    : IWindowsChildSessionProcessStarter
{
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunUseSessionId = 4;
    private const int TaskRunLevelLua = 0;
    private const int TaskActionExec = 0;
    private static readonly TimeSpan AgentHandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly string? _configuredAgentPath;

    /// <summary>
    /// 创建默认使用部署目录或环境变量查找会话代理的启动后端。
    /// </summary>
    public WindowsTaskSchedulerChildSessionProcessStarter()
    {
    }

    /// <summary>
    /// 创建使用显式会话代理路径的启动后端。
    /// </summary>
    /// <param name="sessionAgentPath">会话代理 EXE 的绝对路径。</param>
    /// <exception cref="ArgumentException">
    /// 路径为空白时抛出。
    /// </exception>
    public WindowsTaskSchedulerChildSessionProcessStarter(string sessionAgentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionAgentPath);
        _configuredAgentPath = Path.GetFullPath(sessionAgentPath);
    }

    /// <summary>
    /// 启动会话内代理、完成一次性握手并返回代理创建的真实目标进程。
    /// </summary>
    /// <param name="targetSessionId">上层已经核验的目标子会话编号。</param>
    /// <param name="request">目标可执行文件、参数和工作目录。</param>
    /// <param name="cancellationToken">取消启动握手的令牌。</param>
    /// <returns>目标会话中的实际新进程对象。</returns>
    /// <exception cref="ChildProcessLaunchException">
    /// 代理不可用、任务计划程序拒绝启动、协议失败或目标身份不匹配时抛出。
    /// </exception>
    public async Task<Process> StartAsync(
        SessionId targetSessionId,
        ChildProcessStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return await ExchangeAsync(targetSessionId, request, async (pipe, response, token) =>
        {
            Process process = ValidateAgentResponse(response, targetSessionId, request);
            try
            {
                await SessionAgentProtocol.WriteAcknowledgementAsync(pipe, true, token).ConfigureAwait(false);
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 复用相同的会话代理认证流程，按指定远端尺寸请求当前桌面的 PNG，不启动用户程序。
    /// </summary>
    /// <param name="target">已核验的活动子会话。</param>
    /// <param name="resolution">截图必须匹配的远端桌面尺寸。</param>
    /// <param name="token">取消取图操作。</param>
    internal Task<byte[]> CaptureAsync(
        SessionId target,
        RemoteDesktopResolution resolution,
        CancellationToken token) =>
        ExchangeAsync(
            target,
            request: null,
            receive: (pipe, response, ct) => SessionAgentProtocol.ReadPngAsync(
                pipe,
                response.ImageLength,
                ct,
                resolution.Width,
                resolution.Height),
            cancellationToken: token,
            captureResolution: resolution);

    /// <summary>
    /// 把一次输入测试投递到指定分身。代理未能运行和 Windows 明确拒绝输入是两种结果，
    /// 只有后者能作为恢复父桌面的依据。
    /// </summary>
    internal Task<bool> ProbeInputAsync(SessionId target, CancellationToken token) =>
        ExchangeAsync(target, null, (_, response, _) => Task.FromResult(response.InputAccepted
            ?? throw new InvalidDataException("分身代理没有返回输入测试结果。")), token, probeInput: true);

    /// <summary>在指定会话发起一次认证的操作。管道结束即通知代理回收尚未接管的进程。</summary>
    /// <typeparam name="T">调用完成后交付给宿主的结果类型。</typeparam>
    /// <param name="targetSessionId">需要投递代理的已核验子会话。</param>
    /// <param name="request">启动参数；截图或复检输入时为空。</param>
    /// <param name="receive">读取并验证与操作对应的响应。</param>
    /// <param name="cancellationToken">取消本次交换。</param>
    /// <param name="captureResolution">截图请求的远端像素尺寸；非截图操作时为空。</param>
    /// <param name="probeInput">是否要求代理在自己的交互桌面测试输入。</param>
    private async Task<T> ExchangeAsync<T>(SessionId targetSessionId, ChildProcessStartRequest? request,
        Func<Stream, SessionAgentLaunchResponse, CancellationToken, Task<T>> receive,
        CancellationToken cancellationToken,
        RemoteDesktopResolution? captureResolution = null, bool probeInput = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int nativeSessionId = ConvertSessionId(targetSessionId);
        string agentPath = ResolveSessionAgentPath();
        string pipeName = $"SideStage-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string authenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        await using NamedPipeServerStream pipe = probeInput ? CreateInputProbePipe(pipeName) : new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        string taskName = BuildTemporaryTaskName();
        object? service = null;
        object? rootFolder = null;
        object? definition = null;
        object? registeredTask = null;

        try
        {
            service = ConnectToTaskScheduler();
            rootFolder = RequireComResult(
                InvokeComMethod(service, "GetFolder", "\\"),
                "GetFolder");
            definition = RequireComResult(
                InvokeComMethod(service, "NewTask", 0),
                "NewTask");

            ConfigureTaskDefinition(definition, agentPath, pipeName, authenticationToken);
            registeredTask = RequireComResult(
                InvokeComMethod(
                    rootFolder,
                    "RegisterTaskDefinition",
                    taskName,
                    definition,
                    TaskCreate,
                    null!,
                    null!,
                    TaskLogonInteractiveToken,
                    null!),
                "RegisterTaskDefinition");

            RunRegisteredTask(registeredTask, nativeSessionId);

            using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            handshakeTimeout.CancelAfter(AgentHandshakeTimeout);

            await pipe.WaitForConnectionAsync(handshakeTimeout.Token).ConfigureAwait(false);
            VerifyPipeClient(pipe, agentPath, nativeSessionId);
            string proof = await SessionAgentProtocol
                .ReadFrameAsync<string>(pipe, handshakeTimeout.Token)
                .ConfigureAwait(false);
            if (!string.Equals(proof, authenticationToken, StringComparison.Ordinal))
            {
                throw new ChildProcessLaunchException(
                    "AuthenticateAgent",
                    "会话代理握手标识不匹配。");
            }

            await SessionAgentProtocol.WriteFrameAsync(
                    pipe,
                    new SessionAgentLaunchRequest(
                        SessionAgentProtocol.CurrentVersion,
                        authenticationToken,
                        nativeSessionId,
                        request?.ExecutablePath ?? string.Empty,
                        request?.WorkingDirectory ?? string.Empty,
                        request?.Arguments.ToArray() ?? [],
                        Capture: captureResolution.HasValue,
                        CaptureWidth: captureResolution?.Width ?? 1920,
                        CaptureHeight: captureResolution?.Height ?? 1080,
                        ProbeInput: probeInput),
                    handshakeTimeout.Token)
                .ConfigureAwait(false);

            SessionAgentLaunchResponse response = await SessionAgentProtocol
                .ReadFrameAsync<SessionAgentLaunchResponse>(pipe, handshakeTimeout.Token)
                .ConfigureAwait(false);

            if (!response.Succeeded || response.SessionId != nativeSessionId)
            {
                throw new ChildProcessLaunchException(response.ErrorStage ?? "VerifyResponse", response.ErrorMessage ?? "代理响应的会话编号不匹配。", response.NativeErrorCode);
            }

            return await receive(pipe, response, handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await TryRejectAgentAsync(pipe).ConfigureAwait(false);
            throw new ChildProcessLaunchException(
                "AgentTimeout",
                "等待子会话代理握手超时。",
                innerException: ex);
        }
        catch (ChildProcessLaunchException)
        {
            await TryRejectAgentAsync(pipe).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryRejectAgentAsync(pipe).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await TryRejectAgentAsync(pipe).ConfigureAwait(false);
            throw CreateLaunchException(
                "TaskScheduler",
                "Windows 任务计划程序或会话代理未能完成子会话进程启动。",
                UnwrapComException(ex));
        }
        finally
        {
            // 临时任务只负责投递代理；进程握手完成后立即清理。
            if (registeredTask is not null)
            {
                DeleteTemporaryTask(rootFolder, taskName);
            }
            ReleaseComObject(registeredTask);
            ReleaseComObject(definition);
            ReleaseComObject(rootFolder);
            ReleaseComObject(service);
        }
    }

    /// <summary>
    /// 输入探针使用普通用户令牌，即使宿主以管理员身份启动也要测试普通程序能否输入。
    /// 管道仅允许本机当前用户。握手仍核对代理映像、会话编号和一次性令牌。
    /// </summary>
    private static NamedPipeServerStream CreateInputProbePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("无法确认输入探针的账户。");
        var security = new PipeSecurity();
        // 拒绝网络登录；访问控制明确使用用户 SID，不使用提权后可能变成管理员组的默认所有者。
        security.SetSecurityDescriptorSddlForm($"D:P(D;;GA;;;NU)(A;;GA;;;{user.Value})");
        security.SetOwner(user);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>
    /// 使用 Windows 内核提供的管道端点 PID 核对代理映像路径和会话编号。
    /// </summary>
    /// <param name="pipe">刚建立连接的本地命名管道。</param>
    /// <param name="agentPath">任务中指定的代理绝对路径。</param>
    /// <param name="expectedSessionId">预期目标会话编号。</param>
    /// <exception cref="ChildProcessLaunchException">
    /// 管道客户端不是目标会话内的指定代理时抛出。
    /// </exception>
    private static void VerifyPipeClient(
        NamedPipeServerStream pipe,
        string agentPath,
        int expectedSessionId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out int processId))
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new ChildProcessLaunchException(
                "AuthenticateAgent",
                $"无法读取命名管道客户端 PID（Win32 {errorCode}）。",
                errorCode);
        }

        using Process agentProcess = Process.GetProcessById(processId);
        string? actualPath = WindowsProcessNativeMethods.TryGetImagePath(processId);
        if (agentProcess.SessionId != expectedSessionId
            || actualPath is null
            || !PathsEqual(actualPath, agentPath))
        {
            throw new ChildProcessLaunchException(
                "AuthenticateAgent",
                "命名管道客户端不是目标会话中的指定启动代理。");
        }
    }

    /// <summary>
    /// 查询已经连接的命名管道客户端进程编号。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out int processId);

    /// <summary>
    /// 把 Core 会话编号转换为 Task Scheduler 接受的有符号整数。
    /// </summary>
    /// <param name="sessionId">Core 会话编号。</param>
    /// <returns>Windows 原生会话编号。</returns>
    private static int ConvertSessionId(SessionId sessionId)
    {
        if (sessionId.Value is 0 or > int.MaxValue)
        {
            throw new ChildProcessLaunchException(
                "ValidateSessionId",
                $"目标会话编号 {sessionId.Value} 超出 Task Scheduler 支持的范围。");
        }

        return checked((int)sessionId.Value);
    }

    /// <summary>
    /// 找到随库部署的会话内启动代理。
    /// </summary>
    /// <returns>存在且为文件的代理绝对路径。</returns>
    /// <exception cref="ChildProcessLaunchException">
    /// 没有找到代理文件时抛出。
    /// </exception>
    private string ResolveSessionAgentPath()
    {
        // 已集成早期版本的宿主可以继续使用旧变量，显式传入的路径始终优先。
        string? configuredPath = _configuredAgentPath
            ?? Environment.GetEnvironmentVariable("SIDESTAGE_SESSION_AGENT_PATH")
            ?? Environment.GetEnvironmentVariable("WINCHILDDESK_SESSION_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullConfiguredPath = Path.GetFullPath(configuredPath);
            if (File.Exists(fullConfiguredPath))
            {
                return fullConfiguredPath;
            }

            throw new ChildProcessLaunchException(
                "ResolveSessionAgent",
                $"指定的会话代理不存在：{fullConfiguredPath}");
        }

        string deployedPath = Path.Combine(
            AppContext.BaseDirectory,
            "SideStage.SessionAgent.exe");
        if (File.Exists(deployedPath))
        {
            return deployedPath;
        }

        throw new ChildProcessLaunchException(
            "ResolveSessionAgent",
            $"未找到会话内启动代理：{deployedPath}。请将 SideStage.SessionAgent.exe 部署到程序集目录，或设置 SIDESTAGE_SESSION_AGENT_PATH。");
    }

    /// <summary>
    /// 连接本机 Task Scheduler 服务。
    /// </summary>
    /// <returns>已连接的 Schedule.Service COM 对象。</returns>
    private static object ConnectToTaskScheduler()
    {
        Type? serviceType = Type.GetTypeFromProgID("Schedule.Service");
        if (serviceType is null)
        {
            throw new ChildProcessLaunchException(
                "ConnectTaskScheduler",
                "当前 Windows 未注册 Schedule.Service，无法在指定子会话中启动进程。");
        }

        object service;
        try
        {
            service = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException(
                    "Schedule.Service COM 对象创建后返回 null。");
            InvokeComMethod(
                service,
                "Connect",
                Type.Missing,
                Type.Missing,
                Type.Missing,
                Type.Missing);
            return service;
        }
        catch (Exception ex) when (ex is not ChildProcessLaunchException)
        {
            throw CreateLaunchException(
                "ConnectTaskScheduler",
                "无法连接本机 Windows Task Scheduler 服务。",
                UnwrapComException(ex));
        }
    }

    /// <summary>
    /// 配置临时任务以在目标交互式会话中运行启动代理。
    /// </summary>
    /// <param name="definition">由 Task Scheduler 创建的任务定义。</param>
    /// <param name="agentPath">代理 EXE 的绝对路径。</param>
    /// <param name="pipeName">一次性命名管道名称。</param>
    /// <param name="authenticationToken">一次性认证令牌。</param>
    private static void ConfigureTaskDefinition(
        object definition,
        string agentPath,
        string pipeName,
        string authenticationToken)
    {
        try
        {
            object registrationInfo = GetComProperty(definition, "RegistrationInfo");
            SetComProperty(
                registrationInfo,
                "Description",
                "SideStage 临时子会话启动代理任务");

            object principal = GetComProperty(definition, "Principal");
            SetComProperty(principal, "LogonType", TaskLogonInteractiveToken);
            SetComProperty(principal, "RunLevel", TaskRunLevelLua);

            object settings = GetComProperty(definition, "Settings");
            SetComProperty(settings, "AllowDemandStart", true);
            SetComProperty(settings, "StartWhenAvailable", true);
            SetComProperty(settings, "DisallowStartIfOnBatteries", false);
            SetComProperty(settings, "StopIfGoingOnBatteries", false);

            object actions = GetComProperty(definition, "Actions");
            object execAction = RequireComResult(
                InvokeComMethod(actions, "Create", TaskActionExec),
                "Actions.Create");
            SetComProperty(execAction, "Path", agentPath);
            SetComProperty(
                execAction,
                "Arguments",
                BuildWindowsArgumentString(
                    ["--pipe-name", pipeName, "--authentication-token", authenticationToken]));
            SetComProperty(execAction, "WorkingDirectory", Path.GetDirectoryName(agentPath)!);
        }
        catch (Exception ex) when (ex is not ChildProcessLaunchException)
        {
            throw CreateLaunchException(
                "ConfigureTask",
                "无法配置交互式 Task Scheduler 启动代理动作。",
                UnwrapComException(ex));
        }
    }

    /// <summary>
    /// 以 TASK_RUN_USE_SESSION_ID 标志将任务提交到目标会话。
    /// </summary>
    /// <param name="registeredTask">已注册任务对象。</param>
    /// <param name="nativeSessionId">目标 Windows 会话编号。</param>
    private static void RunRegisteredTask(object registeredTask, int nativeSessionId)
    {
        try
        {
            _ = InvokeComMethod(
                registeredTask,
                "RunEx",
                null,
                TaskRunUseSessionId,
                nativeSessionId,
                null);
        }
        catch (Exception ex) when (ex is not ChildProcessLaunchException)
        {
            throw CreateLaunchException(
                "RunTask",
                $"Task Scheduler 拒绝在会话 {nativeSessionId} 中运行临时启动代理。",
                UnwrapComException(ex));
        }
    }

    /// <summary>
    /// 读取代理响应，核对代理实际创建的 PID、路径和会话编号。
    /// </summary>
    /// <param name="response">代理返回的启动响应。</param>
    /// <param name="targetSessionId">预期目标子会话编号。</param>
    /// <param name="request">原始目标程序请求。</param>
    /// <returns>已打开并核验的目标进程对象。</returns>
    private static Process ValidateAgentResponse(
        SessionAgentLaunchResponse response,
        SessionId targetSessionId,
        ChildProcessStartRequest request)
    {
        if (!response.Succeeded)
        {
            throw new ChildProcessLaunchException(
                response.ErrorStage ?? "SessionAgent",
                response.ErrorMessage ?? "会话内启动代理报告目标进程启动失败。",
                response.NativeErrorCode);
        }

        if (response.ProcessId <= 0)
        {
            throw new ChildProcessLaunchException(
                "VerifyAgentResponse",
                "会话内启动代理返回了无效目标 PID。");
        }

        if (response.SessionId != checked((int)targetSessionId.Value))
        {
            throw new ChildProcessLaunchException(
                "VerifyAgentResponse",
                $"会话内启动代理报告目标进程属于会话 {response.SessionId}，预期为 {targetSessionId}。");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(response.ProcessId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new ChildProcessLaunchException(
                "VerifyAgentResponse",
                $"无法打开会话内启动代理返回的目标进程 {response.ProcessId}。",
                ex is Win32Exception win32Exception ? win32Exception.NativeErrorCode : null,
                ex);
        }

        try
        {
            // 在代理释放创建句柄前固定实际内核对象；以后不能按 PID 重新认领进程。
            _ = process.SafeHandle;
            if (process.SessionId != checked((int)targetSessionId.Value))
            {
                throw new ChildProcessLaunchException(
                    "VerifyAgentResponse",
                    $"目标进程 {process.Id} 的真实会话不是 {targetSessionId}。");
            }

            string? actualPath = WindowsProcessNativeMethods.TryGetImagePath(process.Id);
            if (actualPath is null || !PathsEqual(actualPath, request.ExecutablePath))
            {
                throw new ChildProcessLaunchException(
                    "VerifyAgentResponse",
                    $"目标进程 {process.Id} 的完整路径无法确认为请求路径。");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在握手失败时尽力向代理发送拒绝确认。
    /// </summary>
    /// <param name="pipe">可能已连接的命名管道。</param>
    private static async Task TryRejectAgentAsync(NamedPipeServerStream pipe)
    {
        if (!pipe.IsConnected)
        {
            return;
        }

        try
        {
            await SessionAgentProtocol.WriteAcknowledgementAsync(
                    pipe,
                    accepted: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // 代理可能已经因超时退出；这里不覆盖启动主错误。
        }
    }

    /// <summary>
    /// 生成当前用户任务目录中不会与已有任务冲突的临时名称。
    /// </summary>
    /// <returns>Task Scheduler 任务名。</returns>
    private static string BuildTemporaryTaskName()
    {
        return $"SideStage-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 删除临时任务定义；清理失败不覆盖启动主错误。
    /// </summary>
    /// <param name="rootFolder">任务根目录 COM 对象。</param>
    /// <param name="taskName">临时任务名。</param>
    private static void DeleteTemporaryTask(object? rootFolder, string taskName)
    {
        if (rootFolder is null)
        {
            return;
        }

        try
        {
            InvokeComMethod(rootFolder, "DeleteTask", taskName, 0);
        }
        catch
        {
            // 清理失败由上层日志观察，不伪装成启动成功，也不覆盖原始异常。
        }
    }

    /// <summary>
    /// 将参数数组按 Windows 命令行规则编码为 Exec 动作参数字符串。
    /// </summary>
    /// <param name="arguments">独立参数数组。</param>
    /// <returns>安全编码后的参数字符串。</returns>
    private static string BuildWindowsArgumentString(IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();

        for (int index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            AppendWindowsArgument(builder, arguments[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 将一个独立参数安全编码成 Windows 命令行中的单个参数。
    /// </summary>
    /// <param name="builder">输出命令行缓冲区。</param>
    /// <param name="argument">需要编码的参数。</param>
    private static void AppendWindowsArgument(StringBuilder builder, string argument)
    {
        bool needsQuotes = argument.Length == 0
            || argument.Any(char.IsWhiteSpace)
            || argument.Contains('"');

        if (!needsQuotes)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int backslashCount = 0;

        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
    }

    /// <summary>
    /// 比较两个 Windows 路径是否指向同一文件。
    /// </summary>
    /// <param name="left">实际进程路径。</param>
    /// <param name="right">请求路径。</param>
    /// <returns>路径相同时返回 true。</returns>
    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 COM IDispatch 读取无参数属性。
    /// </summary>
    /// <param name="target">COM 目标对象。</param>
    /// <param name="propertyName">属性名。</param>
    /// <returns>非空属性值。</returns>
    private static object GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            args: null,
            modifiers: null,
            culture: CultureInfo.InvariantCulture,
            namedParameters: null)
            ?? throw new InvalidOperationException(
                $"Task Scheduler COM 属性 {propertyName} 返回 null。");
    }

    /// <summary>
    /// 通过 COM IDispatch 设置属性。
    /// </summary>
    /// <param name="target">COM 目标对象。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">属性值。</param>
    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            args: [value],
            modifiers: null,
            culture: CultureInfo.InvariantCulture,
            namedParameters: null);
    }

    /// <summary>
    /// 通过 COM IDispatch 调用方法。
    /// </summary>
    /// <param name="target">COM 目标对象。</param>
    /// <param name="methodName">方法名。</param>
    /// <param name="arguments">COM 方法参数。</param>
    /// <returns>COM 方法返回值。</returns>
    private static object? InvokeComMethod(
        object target,
        string methodName,
        params object?[] arguments)
    {
        return target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            target,
            arguments,
            modifiers: null,
            culture: CultureInfo.InvariantCulture,
            namedParameters: null);
    }

    /// <summary>
    /// 把 COM 方法返回值转换为非空对象。
    /// </summary>
    /// <param name="value">COM 方法返回值。</param>
    /// <param name="methodName">产生该返回值的方法。</param>
    /// <returns>非空 COM 对象。</returns>
    private static object RequireComResult(object? value, string methodName)
    {
        return value
            ?? throw new ChildProcessLaunchException(
                "TaskSchedulerCom",
                $"Task Scheduler COM 方法 {methodName} 返回了 null。");
    }

    /// <summary>
    /// 解包反射调用包装的底层 COM 异常。
    /// </summary>
    /// <param name="exception">原始异常。</param>
    /// <returns>最底层异常。</returns>
    private static Exception UnwrapComException(Exception exception)
    {
        Exception current = exception;
        while (current is TargetInvocationException { InnerException: Exception innerException })
        {
            current = innerException;
        }

        return current;
    }

    /// <summary>
    /// 创建包含稳定失败阶段和可选原生错误码的统一异常。
    /// </summary>
    /// <param name="stage">失败阶段。</param>
    /// <param name="message">错误摘要。</param>
    /// <param name="exception">底层异常。</param>
    /// <returns>结构化启动异常。</returns>
    private static ChildProcessLaunchException CreateLaunchException(
        string stage,
        string message,
        Exception exception)
    {
        int? nativeErrorCode = exception switch
        {
            Win32Exception win32 => win32.NativeErrorCode,
            COMException com => com.ErrorCode,
            _ => null,
        };
        return new ChildProcessLaunchException(stage, message, nativeErrorCode, exception);
    }

    /// <summary>
    /// 释放一个可能的 COM 运行时可调用包装对象。
    /// </summary>
    /// <param name="value">需要释放的对象。</param>
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
