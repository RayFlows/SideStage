/*
 * 文件名称：Program.cs
 * 文件职责：
 *   实现运行在目标 Windows 子会话内的一次性进程启动代理。
 *
 * 工作流程：
 *   1. 从任务计划程序参数读取随机命名管道名称和认证令牌。
 *   2. 连接宿主创建的管道，读取包含预期 SessionId 的启动请求。
 *   3. 核对自身当前 SessionId，使用 ProcessStartInfo.ArgumentList 在当前会话
 *      启动目标程序，不经过 cmd.exe、PowerShell 或 shell 字符串拼接。
 *   4. 将目标 PID 和真实 SessionId 返回宿主，并等待宿主接管确认。
 *   5. 宿主拒绝、超时或协议失败时终止刚创建的目标进程。
 *
 * 安全边界：
 *   - 代理不接受任意远程地址，不修改目标会话，不读取密码或生产配置。
 *   - 认证令牌只在本次管道握手中使用，错误消息不回显目标参数。
 *   - 代理本身不判断业务任务是否成功，只负责一次进程创建和归属证明。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Drawing.Imaging;
using System.Security;
using SideStage.Core.Processes;

namespace SideStage.SessionAgent;

/// <summary>
/// 会话内启动代理的异步程序入口。
/// </summary>
internal static class Program
{
    private static readonly TimeSpan PipeHandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 解析命令行、连接宿主并执行一次受控目标进程启动。
    /// </summary>
    /// <param name="args">Task Scheduler 传入的管道名称和一次性认证令牌。</param>
    /// <returns>成功完成握手返回 0；协议或启动失败返回 1。</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            AgentArguments agentArguments = ParseArguments(args);
            using NamedPipeClientStream pipe = new(
                ".",
                agentArguments.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using CancellationTokenSource handshakeTimeout = new();
            handshakeTimeout.CancelAfter(PipeHandshakeTimeout);

            await pipe.ConnectAsync(handshakeTimeout.Token).ConfigureAwait(false);
            await SessionAgentProtocol.WriteFrameAsync(
                    pipe,
                    agentArguments.AuthenticationToken,
                    handshakeTimeout.Token)
                .ConfigureAwait(false);
            SessionAgentLaunchRequest request = await SessionAgentProtocol
                .ReadFrameAsync<SessionAgentLaunchRequest>(pipe, handshakeTimeout.Token)
                .ConfigureAwait(false);

            Process? process = null;
            bool accepted = false;
            try
            {
                ValidateRequest(request, agentArguments);
                if (request.ProbeInput)
                {
                    bool acceptedInput = DesktopInputProbe.TryZeroMove();
                    await SessionAgentProtocol.WriteFrameAsync(pipe, new SessionAgentLaunchResponse(
                        true, 0, request.ExpectedSessionId, null, null, null, InputAccepted: acceptedInput),
                        handshakeTimeout.Token).ConfigureAwait(false);
                    return 0;
                }
                if (request.Capture)
                {
                    await CaptureDesktopAsync(
                            pipe,
                            request.ExpectedSessionId,
                            request.CaptureWidth,
                            request.CaptureHeight,
                            handshakeTimeout.Token)
                        .ConfigureAwait(false);
                    return 0;
                }

                process = StartTargetProcess(request);
                int actualSessionId = ReadProcessSessionId(process);
                if (actualSessionId != request.ExpectedSessionId)
                {
                    TryTerminate(process);
                    await SendFailureAsync(
                            pipe,
                            request,
                            "VerifyTargetSession",
                            $"目标进程 {process.Id} 的会话编号为 {actualSessionId}，预期为 {request.ExpectedSessionId}。",
                            nativeErrorCode: null,
                            handshakeTimeout.Token)
                        .ConfigureAwait(false);
                    return 1;
                }

                await SessionAgentProtocol.WriteFrameAsync(
                        pipe,
                        new SessionAgentLaunchResponse(
                            Succeeded: true,
                            ProcessId: process.Id,
                            SessionId: actualSessionId,
                            NativeErrorCode: null,
                            ErrorStage: null,
                            ErrorMessage: null),
                        handshakeTimeout.Token)
                    .ConfigureAwait(false);

                accepted = await SessionAgentProtocol
                    .ReadAcknowledgementAsync(pipe, handshakeTimeout.Token)
                    .ConfigureAwait(false);
                if (!accepted)
                {
                    TryTerminate(process);
                    return 1;
                }

                return 0;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or SecurityException or ArgumentException or IOException or System.Runtime.InteropServices.ExternalException)
            {
                TryTerminate(process);
                await SendFailureAsync(
                        pipe,
                        request,
                        request.Capture ? "CaptureDesktop" : "StartTargetProcess",
                        request.Capture ? $"分身取图失败（{ex.TargetSite?.Name}）。{ex.Message}" : "会话内启动代理无法创建或核验目标进程。",
                        ex is Win32Exception win32Exception ? win32Exception.NativeErrorCode : ex.HResult,
                        handshakeTimeout.Token)
                    .ConfigureAwait(false);
                return 1;
            }
            finally
            {
                // 超时、管道断开或宿主取消也必须清理；不只处理显式拒绝。
                if (!accepted)
                {
                    TryTerminate(process);
                }

                process?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception)
        {
            // 代理是一次性后台进程；具体错误由宿主的握手超时和阶段异常负责报告。
            return 1;
        }
    }

    /// <summary>
    /// 解析代理自身的命令行参数并拒绝未知或重复选项。
    /// </summary>
    /// <param name="args">原始命令行参数。</param>
    /// <returns>已校验的代理参数。</returns>
    /// <exception cref="ArgumentException">
    /// 参数缺失、重复或包含非法值时抛出。
    /// </exception>
    private static AgentArguments ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? pipeName = null;
        string? authenticationToken = null;

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"代理参数 {option} 缺少值。", nameof(args));
            }

            string value = args[++index];
            switch (option)
            {
                case "--pipe-name" when pipeName is null:
                    pipeName = value;
                    break;
                case "--authentication-token" when authenticationToken is null:
                    authenticationToken = value;
                    break;
                case "--pipe-name":
                    throw new ArgumentException("代理参数 --pipe-name 重复。", nameof(args));
                case "--authentication-token":
                    throw new ArgumentException("代理参数 --authentication-token 重复。", nameof(args));
                default:
                    throw new ArgumentException($"代理收到未知参数 {option}。", nameof(args));
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(authenticationToken))
        {
            throw new ArgumentException(
                "代理必须同时收到 --pipe-name 和 --authentication-token。",
                nameof(args));
        }

        return new AgentArguments(pipeName, authenticationToken);
    }

    /// <summary>
    /// 核对宿主请求的协议版本、认证令牌和预期目标会话。
    /// </summary>
    /// <param name="request">宿主发送的启动请求。</param>
    /// <param name="agentArguments">代理命令行中的认证参数。</param>
    private static void ValidateRequest(
        SessionAgentLaunchRequest request,
        AgentArguments agentArguments)
    {
        if (request.ProtocolVersion != SessionAgentProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"代理不支持协议版本 {request.ProtocolVersion}。");
        }

        if (!CryptographicEquals(
                request.AuthenticationToken,
                agentArguments.AuthenticationToken))
        {
            throw new SecurityException("会话代理认证令牌不匹配。");
        }

        using Process current = Process.GetCurrentProcess();
        int currentSessionId = current.SessionId;
        if (request.ExpectedSessionId != currentSessionId)
        {
            throw new InvalidOperationException(
                $"代理当前位于会话 {currentSessionId}，但宿主预期会话为 {request.ExpectedSessionId}。");
        }

        if (request.ProbeInput)
        {
            if (request.Capture || request.ExecutablePath.Length != 0)
                throw new InvalidDataException("输入测试不能与截图或启动程序合并。");
            return;
        }

        if (request.Capture)
        {
            if (request.CaptureWidth is < 200 or > 8192
                || request.CaptureHeight is < 200 or > 8192)
            {
                throw new InvalidDataException("截图请求尺寸超出支持的远端桌面范围。");
            }

            return;
        }

        if (!Path.IsPathFullyQualified(request.ExecutablePath)
            || !File.Exists(request.ExecutablePath))
        {
            throw new FileNotFoundException(
                "代理请求的目标可执行文件不存在或不是绝对路径。");
        }

        if (!Path.IsPathFullyQualified(request.WorkingDirectory)
            || !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                "代理请求的目标工作目录不存在或不是绝对路径。");
        }
    }

    /// <summary>
    /// 仅从代理自身的桌面按请求尺寸取图；像素直接通过现有管道传输，不落盘。
    /// </summary>
    /// <param name="pipe">已认证的调用方管道。</param>
    /// <param name="session">已核对的本机子会话编号。</param>
    /// <param name="expectedWidth">调用方预期的远端桌面宽度。</param>
    /// <param name="expectedHeight">调用方预期的远端桌面高度。</param>
    /// <param name="token">限制单次编码与传输的等待时间。</param>
    private static async Task CaptureDesktopAsync(
        Stream pipe,
        int session,
        int expectedWidth,
        int expectedHeight,
        CancellationToken token)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        if (bounds.Width != expectedWidth || bounds.Height != expectedHeight)
        {
            throw new InvalidOperationException(
                $"当前子桌面是 {bounds.Width}×{bounds.Height}，预期为 {expectedWidth}×{expectedHeight}。");
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("当前子桌面没有有效的可捕获区域。");
        }

        using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        using MemoryStream buffer = new();
        bitmap.Save(buffer, ImageFormat.Png);
        await SessionAgentProtocol.WriteFrameAsync(pipe, new SessionAgentLaunchResponse(true, 0, session, null, null, null, checked((int)buffer.Length)), token).ConfigureAwait(false);
        buffer.Position = 0;
        await buffer.CopyToAsync(pipe, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 在代理当前所在会话中启动目标程序，不经过 shell。
    /// </summary>
    /// <param name="request">宿主发送的目标程序请求。</param>
    /// <returns>新创建的目标进程对象。</returns>
    private static Process StartTargetProcess(SessionAgentLaunchRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 返回了 null。");
    }

    /// <summary>
    /// 读取代理刚创建的目标进程 SessionId。
    /// </summary>
    /// <param name="process">目标进程对象。</param>
    /// <returns>Windows 真实会话编号。</returns>
    private static int ReadProcessSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException(
                $"无法读取目标进程 {process.Id} 的会话编号。",
                ex);
        }
    }

    /// <summary>
    /// 向宿主发送不包含命令行参数的结构化失败响应。
    /// </summary>
    /// <param name="pipe">当前握手管道。</param>
    /// <param name="request">原始请求，用于回传协议版本。</param>
    /// <param name="stage">失败阶段。</param>
    /// <param name="message">错误摘要。</param>
    /// <param name="nativeErrorCode">可选 Win32 错误码。</param>
    /// <param name="cancellationToken">取消发送的令牌。</param>
    private static async Task SendFailureAsync(
        Stream pipe,
        SessionAgentLaunchRequest request,
        string stage,
        string message,
        int? nativeErrorCode,
        CancellationToken cancellationToken)
    {
        await SessionAgentProtocol.WriteFrameAsync(
                pipe,
                new SessionAgentLaunchResponse(
                    Succeeded: false,
                    ProcessId: 0,
                    SessionId: request.ExpectedSessionId,
                    NativeErrorCode: nativeErrorCode,
                    ErrorStage: stage,
                    ErrorMessage: message),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 尽力终止握手未接管的目标进程，不覆盖原始错误。
    /// </summary>
    /// <param name="process">可能已经创建的目标进程。</param>
    private static void TryTerminate(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 代理即将退出，清理失败交给宿主的进程状态报告。
        }
    }

    /// <summary>
    /// 以固定时间比较两个认证令牌，避免短路比较暴露令牌长度信息。
    /// </summary>
    /// <param name="left">请求中的令牌。</param>
    /// <param name="right">命令行中的令牌。</param>
    /// <returns>令牌相同时返回 true。</returns>
    private static bool CryptographicEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        int difference = 0;
        for (int index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }

    /// <summary>
    /// 保存已经校验的代理启动参数。
    /// </summary>
    /// <param name="PipeName">命名管道名称。</param>
    /// <param name="AuthenticationToken">一次性认证令牌。</param>
    private sealed record AgentArguments(
        string PipeName,
        string AuthenticationToken);
}
