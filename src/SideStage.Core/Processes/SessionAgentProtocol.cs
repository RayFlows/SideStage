/*
 * 文件名称：SessionAgentProtocol.cs
 * 文件职责：
 *   定义宿主与子会话启动代理之间有长度边界的内部消息协议。
 *
 * 安全与健壮性约束：
 *   - 每条消息有显式长度上限，拒绝空帧、超长帧和无效 JSON。
 *   - 协议携带一次性随机令牌和预期 SessionId，代理必须逐项核验。
 *   - 代理成功启动目标进程后必须等待宿主确认；未获确认时代理终止新进程。
 *   - 该协议只用于同一用户的本机命名管道，不保存密码或访问令牌。
 *
 * 可见性：
 *   本文件中的类型只对 SideStage.Windows 和 SideStage.SessionAgent 可见。
 */

using System.Buffers.Binary;
using System.Text.Json;

namespace SideStage.Core.Processes;

/// <summary>
/// 定义 SideStage 宿主与会话内启动代理的内部消息协议。
/// </summary>
internal static class SessionAgentProtocol
{
    internal const int CurrentVersion = 2;
    private const int MaximumFrameBytes = 1024 * 1024;

    /// <summary>
    /// 将对象序列化为带长度前缀的 JSON 帧并写入流。
    /// </summary>
    /// <typeparam name="TMessage">消息类型。</typeparam>
    /// <param name="stream">双向命名管道流。</param>
    /// <param name="message">需要发送的消息。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    public static async Task WriteFrameAsync<TMessage>(
        Stream stream,
        TMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length is 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"内部启动协议帧长度 {payload.Length} 超出允许范围。");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从流中读取并反序列化一个带长度前缀的 JSON 帧。
    /// </summary>
    /// <typeparam name="TMessage">期望的消息类型。</typeparam>
    /// <param name="stream">双向命名管道流。</param>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    /// <param name="maximumBytes">本次消息允许的最大字节数。</param>
    /// <returns>反序列化后的消息。</returns>
    /// <exception cref="InvalidDataException">
    /// 帧长度无效或 JSON 为空时抛出。
    /// </exception>
    public static async Task<TMessage> ReadFrameAsync<TMessage>(
        Stream stream,
        CancellationToken cancellationToken,
        int maximumBytes = MaximumFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"内部启动协议帧长度 {payloadLength} 超出允许范围。");
        }

        byte[] payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<TMessage>(payload)
            ?? throw new InvalidDataException("内部启动协议帧没有包含有效消息。");
    }

    /// <summary>
    /// 读取宿主对启动代理的单字节确认，只有值 1 才表示宿主已接管新进程。
    /// </summary>
    /// <param name="stream">双向命名管道流。</param>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    /// <returns>宿主接受进程时返回 true。</returns>
    public static async Task<bool> ReadAcknowledgementAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] value = new byte[1];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return value[0] == 1;
    }

    /// <summary>
    /// 向启动代理发送单字节确认或拒绝。
    /// </summary>
    /// <param name="stream">双向命名管道流。</param>
    /// <param name="accepted">宿主是否接受代理创建的进程。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    public static async Task WriteAcknowledgementAsync(
        Stream stream,
        bool accepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] value = [accepted ? (byte)1 : (byte)0];
        await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>截屏单独发送二进制 PNG，并核对响应尺寸后再将图像交给调用方。</summary>
    /// <param name="stream">本次已认证的本机管道。</param>
    /// <param name="length">代理先前返回的图像字节数。</param>
    /// <param name="token">取消读取并关闭本次一次性连接。</param>
    /// <param name="expectedWidth">本次请求的远端桌面宽度。</param>
    /// <param name="expectedHeight">本次请求的远端桌面高度。</param>
    internal static async Task<byte[]> ReadPngAsync(
        Stream stream,
        int length,
        CancellationToken token,
        int expectedWidth,
        int expectedHeight)
    {
        const int maximumPngBytes = 256 * 1024 * 1024;
        if (length is < 24 or > maximumPngBytes)
        {
            throw new InvalidDataException("分身返回的 PNG 长度无效。");
        }

        byte[] image = new byte[length];
        await stream.ReadExactlyAsync(image, token).ConfigureAwait(false);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!image.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new InvalidDataException("分身未返回 PNG 图像。");
        }

        // PNG 的 IHDR 紧跟 8 字节签名；读回尺寸可防止过期或其他桌面画面混入当前请求。
        if (!image.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("分身 PNG 缺少有效的 IHDR 图像头。");
        }

        uint actualWidth = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16, 4));
        uint actualHeight = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20, 4));
        if (actualWidth != expectedWidth || actualHeight != expectedHeight)
        {
            throw new InvalidDataException(
                $"分身 PNG 尺寸为 {actualWidth}×{actualHeight}，预期 {expectedWidth}×{expectedHeight}。");
        }

        return image;
    }
}

/// <summary>
/// 表示宿主向会话内代理发送的一次性启动请求。
/// </summary>
/// <param name="ProtocolVersion">协议版本。</param>
/// <param name="AuthenticationToken">匹配命名管道会话的一次性随机令牌。</param>
/// <param name="ExpectedSessionId">宿主预期的子会话编号。</param>
/// <param name="ExecutablePath">目标程序完整路径。</param>
/// <param name="WorkingDirectory">目标程序工作目录。</param>
/// <param name="Arguments">目标程序参数数组。</param>
/// <param name="Capture">是否只请求代理捕获当前桌面而不启动目标进程。</param>
/// <param name="CaptureWidth">截图操作预期的桌面像素宽度。</param>
/// <param name="CaptureHeight">截图操作预期的桌面像素高度。</param>
internal sealed record SessionAgentLaunchRequest(
    int ProtocolVersion,
    string AuthenticationToken,
    int ExpectedSessionId,
    string ExecutablePath,
    string WorkingDirectory,
    string[] Arguments,
    bool Capture = false,
    int CaptureWidth = 1920,
    int CaptureHeight = 1080);

/// <summary>
/// 表示会话内代理启动目标程序后的结构化结果。
/// </summary>
/// <param name="Succeeded">是否成功创建目标进程。</param>
/// <param name="ProcessId">代理实际创建的 PID。</param>
/// <param name="SessionId">代理读取到的目标进程 SessionId。</param>
/// <param name="NativeErrorCode">启动失败时可用的 Windows 原生错误码。</param>
/// <param name="ErrorStage">失败阶段。</param>
/// <param name="ErrorMessage">不包含命令行参数的诊断信息。</param>
/// <param name="ImageLength">截图响应的 PNG 字节数。</param>
internal sealed record SessionAgentLaunchResponse(
    bool Succeeded,
    int ProcessId,
    int SessionId,
    int? NativeErrorCode,
    string? ErrorStage,
    string? ErrorMessage,
    int ImageLength = 0);
