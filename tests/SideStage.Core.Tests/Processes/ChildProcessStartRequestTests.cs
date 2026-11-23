/*
 * 文件名称：ChildProcessStartRequestTests.cs
 * 文件职责：
 *   验证跨会话进程启动请求的路径规范化、参数隔离和输入拒绝规则。
 *
 * 测试边界：
 *   - 不创建 Windows 子会话，不调用 Task Scheduler，不启动业务程序。
 *   - 使用当前测试运行时已经存在的可执行文件作为合法路径样本。
 *   - 这里只验证 Core 值对象，不验证 Windows 具体启动后端。
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Processes;

namespace SideStage.Core.Tests.Processes;

/// <summary>
/// 验证受管进程启动请求不会把未经校验的路径和参数直接传给平台层。
/// </summary>
[TestClass]
public sealed class ChildProcessStartRequestTests
{
    /// <summary>
    /// 验证合法路径会被规范化，参数集合会被复制为只读集合。
    /// </summary>
    [TestMethod]
    public void Constructor_NormalizesPathsAndCopiesArguments()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new AssertFailedException("测试进程没有可用的 Environment.ProcessPath。");
        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new AssertFailedException("测试进程路径没有父目录。");

        string[] arguments = ["--name", "value with spaces"];
        ChildProcessStartRequest request = new(
            executablePath,
            arguments,
            workingDirectory);

        arguments[0] = "changed";

        Assert.AreEqual(Path.GetFullPath(executablePath), request.ExecutablePath);
        Assert.AreEqual(Path.GetFullPath(workingDirectory), request.WorkingDirectory);
        CollectionAssert.AreEqual(
            new[] { "--name", "value with spaces" },
            request.Arguments.ToArray());
    }

    /// <summary>
    /// 验证不存在的可执行文件不会进入平台启动层。
    /// </summary>
    [TestMethod]
    public void Constructor_RejectsMissingExecutable()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"SideStage-missing-{Guid.NewGuid():N}.exe");

        Assert.ThrowsExactly<ArgumentException>(
            () => new ChildProcessStartRequest(missingPath));
    }

    /// <summary>
    /// 验证参数中的空字符被拒绝，避免破坏 Windows 命令行边界。
    /// </summary>
    [TestMethod]
    public void Constructor_RejectsNullCharacterInArgument()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new AssertFailedException("测试进程没有可用的 Environment.ProcessPath。");

        Assert.ThrowsExactly<ArgumentException>(
            () => new ChildProcessStartRequest(executablePath, ["bad\0argument"]));
    }
}
