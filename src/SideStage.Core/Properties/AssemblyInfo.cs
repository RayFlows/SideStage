/*
 * 文件名称：AssemblyInfo.cs
 * 文件职责：
 *   声明桌面分身内部启动协议仅对受控 Windows 启动程序集和会话代理程序集可见。
 *
 * 设计边界：
 *   - 协议 DTO 不作为业务调用方可随意构造的公开 API。
 *   - 只授予仓库内两个固定程序集访问权限，不使用开放式 InternalsVisibleTo。
 */

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SideStage.Windows")]
[assembly: InternalsVisibleTo("SideStage.SessionAgent")]
[assembly: InternalsVisibleTo("SideStage.Core.Tests")]
