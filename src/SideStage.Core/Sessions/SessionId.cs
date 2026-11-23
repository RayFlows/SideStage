/*
 * 文件名称：SessionId.cs
 * 文件职责：
 *   封装 Windows 会话编号，避免在公开 API 中到处传递没有语义的 uint。
 *
 * 设计边界：
 *   - Windows 使用 0xFFFFFFFF 表示“没有子会话”或无效会话。
 *   - 该哨兵值不能进入正常的 SessionId 实例。
 *   - 本类型只表达编号，不负责查询、创建、连接或结束会话。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 表示一个有效的 Windows 会话编号。
/// </summary>
public readonly record struct SessionId
{
    /// <summary>
    /// 创建一个会话编号。
    /// </summary>
    /// <param name="value">Windows 返回的会话编号。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 当编号等于 Windows 的无效会话哨兵值时抛出。
    /// </exception>
    public SessionId(uint value)
    {
        if (value == uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "0xFFFFFFFF 是 Windows 的无效会话哨兵值，不能作为有效会话编号。");
        }

        Value = value;
    }

    /// <summary>
    /// 获取 Windows 会话编号。
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// 将会话编号格式化为便于日志检索的文本。
    /// </summary>
    /// <returns>形如 Session(3) 的会话编号文本。</returns>
    public override string ToString() => $"Session({Value})";
}
