/*
 * 文件名称：SessionAgentProtocolTests.cs
 * 文件职责：
 *   验证宿主与会话代理之间的长度前缀消息和 PNG 边界检查。
 *
 * 测试边界：
 *   - 只使用内存流，不创建命名管道、计划任务或 Windows 子会话。
 *   - 重点覆盖消息完整性和恶意长度拒绝，避免协议层把无限数据交给业务。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using SideStage.Core.Processes;

namespace SideStage.Core.Tests.Processes;

/// <summary>
/// 覆盖内部启动协议的序列化、长度限制和图像魔数校验。
/// </summary>
[TestClass]
public sealed class SessionAgentProtocolTests
{
    /// <summary>
    /// 验证 JSON 帧写入后可以从同一字节序列完整读回。
    /// </summary>
    [TestMethod]
    public async Task FrameRoundTrip_PreservesMessage()
    {
        using MemoryStream stream = new();
        SessionAgentLaunchResponse expected = new(
            Succeeded: true,
            ProcessId: 123,
            SessionId: 9,
            NativeErrorCode: null,
            ErrorStage: null,
            ErrorMessage: null);

        await SessionAgentProtocol.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;

        SessionAgentLaunchResponse actual =
            await SessionAgentProtocol.ReadFrameAsync<SessionAgentLaunchResponse>(
                stream,
                CancellationToken.None);

        Assert.AreEqual(expected, actual);
    }

    /// <summary>
    /// 验证带自定义分辨率的截图请求通过当前版本的代理协议完整往返。
    /// </summary>
    [TestMethod]
    public async Task CaptureRequestRoundTrip_PreservesRequestedDimensions()
    {
        using MemoryStream stream = new();
        SessionAgentLaunchRequest expected = new(
            ProtocolVersion: SessionAgentProtocol.CurrentVersion,
            AuthenticationToken: "one-time-test-token",
            ExpectedSessionId: 9,
            ExecutablePath: string.Empty,
            WorkingDirectory: string.Empty,
            Arguments: [],
            Capture: true,
            CaptureWidth: 2560,
            CaptureHeight: 1440);

        await SessionAgentProtocol.WriteFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;

        SessionAgentLaunchRequest actual =
            await SessionAgentProtocol.ReadFrameAsync<SessionAgentLaunchRequest>(
                stream,
                CancellationToken.None);

        Assert.AreEqual(expected.ProtocolVersion, actual.ProtocolVersion);
        Assert.AreEqual(expected.AuthenticationToken, actual.AuthenticationToken);
        Assert.AreEqual(expected.ExpectedSessionId, actual.ExpectedSessionId);
        Assert.AreEqual(expected.ExecutablePath, actual.ExecutablePath);
        Assert.AreEqual(expected.WorkingDirectory, actual.WorkingDirectory);
        CollectionAssert.AreEqual(expected.Arguments, actual.Arguments);
        Assert.AreEqual(expected.Capture, actual.Capture);
        Assert.AreEqual(expected.CaptureWidth, actual.CaptureWidth);
        Assert.AreEqual(expected.CaptureHeight, actual.CaptureHeight);
    }

    /// <summary>
    /// 验证非 PNG 数据不会被截图读取器当成有效画面返回。
    /// </summary>
    [TestMethod]
    public async Task ReadPngAsync_RejectsInvalidSignature()
    {
        using MemoryStream stream = new(new byte[24]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => SessionAgentProtocol.ReadPngAsync(
                stream,
                24,
                CancellationToken.None,
                expectedWidth: 1920,
                expectedHeight: 1080));
    }

    /// <summary>
    /// 验证截图协议会拒绝尺寸与当前预览设置不同的 PNG 响应头。
    /// </summary>
    [TestMethod]
    public async Task ReadPngAsync_RejectsUnexpectedDesktopDimensions()
    {
        byte[] header = CreatePngHeader(width: 2560, height: 1440);
        using MemoryStream stream = new(header);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => SessionAgentProtocol.ReadPngAsync(
                stream,
                header.Length,
                CancellationToken.None,
                expectedWidth: 1920,
                expectedHeight: 1080));
    }

    /// <summary>
    /// 为尺寸协议测试生成带 PNG 签名和 IHDR 尺寸字段的最小数据头。
    /// </summary>
    /// <param name="width">写入的 PNG 像素宽度。</param>
    /// <param name="height">写入的 PNG 像素高度。</param>
    /// <returns>24 字节的 PNG 文件头片段。</returns>
    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] header = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), checked((uint)height));
        return header;
    }
}
