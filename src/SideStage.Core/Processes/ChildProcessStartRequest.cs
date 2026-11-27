/*
 * 文件名称：ChildProcessStartRequest.cs
 * 文件职责：
 *   封装启动一个受管子会话进程所需的可验证参数。
 *
 * 设计边界：
 *   - 可执行文件和工作目录在构造时规范化为绝对路径。
 *   - 参数按独立字符串保存，平台实现必须使用参数数组，不能拼接 shell 命令。
 *   - 请求不包含会话编号；目标会话由启动器从当前父会话的子会话关系中核验。
 */

using System.Collections.ObjectModel;

namespace SideStage.Core.Processes;

/// <summary>
/// 表示启动一个子会话进程的不可变请求。
/// </summary>
public sealed class ChildProcessStartRequest
{
    /// <summary>
    /// 创建一个受管进程启动请求。
    /// </summary>
    /// <param name="executablePath">目标可执行文件的路径。</param>
    /// <param name="arguments">传递给目标程序的独立命令行参数；省略时为空参数集合。</param>
    /// <param name="workingDirectory">可选工作目录；省略时使用可执行文件所在目录。</param>
    /// <exception cref="ArgumentException">
    /// 可执行文件或工作目录不是有效的现存路径，或参数中包含空字符时抛出。
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="executablePath"/> 为 null 时抛出。
    /// </exception>
    public ChildProcessStartRequest(
        string executablePath,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new ArgumentException(
                $"目标可执行文件不存在：{fullExecutablePath}",
                nameof(executablePath));
        }

        string resolvedWorkingDirectory = Path.GetFullPath(
            workingDirectory ?? Path.GetDirectoryName(fullExecutablePath)!);
        if (!Directory.Exists(resolvedWorkingDirectory))
        {
            throw new ArgumentException(
                $"目标工作目录不存在：{resolvedWorkingDirectory}",
                nameof(workingDirectory));
        }

        string[] argumentArray = arguments?.ToArray() ?? [];
        for (int index = 0; index < argumentArray.Length; index++)
        {
            string? argument = argumentArray[index];
            if (argument is null)
            {
                throw new ArgumentException(
                    $"参数列表的第 {index} 项为 null。",
                    nameof(arguments));
            }

            if (argument.Contains('\0'))
            {
                throw new ArgumentException(
                    $"参数列表的第 {index} 项包含 Windows 命令行不支持的空字符。",
                    nameof(arguments));
            }
        }

        ExecutablePath = fullExecutablePath;
        WorkingDirectory = resolvedWorkingDirectory;
        Arguments = new ReadOnlyCollection<string>(argumentArray);
    }

    /// <summary>
    /// 获取规范化后的可执行文件绝对路径。
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// 获取规范化后的工作目录绝对路径。
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// 获取按原顺序保存的不可变命令行参数集合。
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }
}
