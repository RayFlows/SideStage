/*
 * 文件职责：以 Windows 登录启动项原有的用户身份，运行当前用户主动托管的一个入口。
 * 部署方式：构建时嵌入宿主，首次关闭启动项才放到稳定的用户目录。独立于安装位置，
 * 即使便携版被移动，主桌面的原命令和快捷方式仍可启动。
 * 信任边界：管道只返回一个会话判断位，从不传递命令。代理没有提权入口。
 */
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SideStage.StartupGate
{
    internal static class Dispatcher
    {
        /// <summary>按用户和代理目录隔离判定通道，使不同测试目录不能影响本机实际登录。</summary>
        internal static string PipeName(string directory)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            using (var hash = SHA256.Create())
            {
                string folder = Path.GetFullPath(directory).TrimEnd('\\').ToUpperInvariant();
                string tag = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(folder)), 0, 8);
                return "SideStage.Startup." + (identity.User ?? throw new InvalidOperationException()).Value + "." + tag;
            }
        }

        /// <summary>限定为本机磁盘的绝对路径，防止枚举或加载图标时访问网络共享。</summary>
        internal static bool LocalPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.Length > 3 && char.IsLetter(path[0])
                && path[1] == ':' && path[2] == '\\' && !path.Substring(2).Contains(":")
                && new DriveInfo(path.Substring(0, 3)).DriveType == DriveType.Fixed;
        }

        /// <summary>只解释明确指向本机 EXE 的 Run 命令；原参数原样传递，不启动命令解释器。</summary>
        internal static bool TrySplit(string raw, out string program, out string arguments)
        {
            program = arguments = "";
            string text = raw.TrimStart();
            bool quoted = text.StartsWith("\"", StringComparison.Ordinal);
            int end = quoted ? text.IndexOf('"', 1) : text.IndexOfAny(new[] { ' ', '\t' });
            if (quoted && (end < 1 || end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1]))) return false;
            if (end < 0) end = text.Length;
            program = quoted ? text.Substring(1, end - 1) : text.Substring(0, end);
            arguments = text.Substring(quoted ? end + 1 : end).TrimStart();
            return LocalPath(program) && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

#if STARTUP_GATE
        /// <summary>只接受一个随机编号。先读回原入口，再在已确认的分身中跳过，其他会话照常运行。</summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Guid id;
            if (args.Length != 1 || !Guid.TryParseExact(args[0], "N", out id)) return 2;
            try
            {
                // 个人备份可由当前用户编辑，提权令牌和系统账户不能用来执行其中的命令。
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    int elevation, length;
                    if (identity.IsSystem || !GetTokenInformation(identity.Token, 18, out elevation, sizeof(int), out length)
                        || elevation == 2) return 5;
                }
                string stem = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, id.ToString("N"));
                ProcessStartInfo start;
                bool task = File.Exists(stem + ".task");
                if (task) start = TaskAction(stem + ".task");
                else if (File.Exists(stem + ".lnk"))
                    start = new ProcessStartInfo(stem + ".lnk") { UseShellExecute = true };
                else
                {
                    bool expand = File.Exists(stem + ".expand");
                    string command = File.ReadAllText(stem + (expand ? ".expand" : ".run"));
                    string program, arguments;
                    if (!TrySplit(expand ? Environment.ExpandEnvironmentVariables(command) : command, out program, out arguments)) return 3;
                    start = new ProcessStartInfo(program, arguments) { UseShellExecute = false };
                }
                if (!IsManagedChild())
                    using (var process = Process.Start(start))
                    {
                        if (task) { process.WaitForExit(); return process.ExitCode; }
                    }
                return 0;
            }
            catch (Exception) { return 1; }
        }

        /// <summary>恢复计划任务原来的程序和参数。工作目录从任务继承，结束状态反馈给任务计划程序。</summary>
        private static ProcessStartInfo TaskAction(string path)
        {
            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(path);
            var node = xml.DocumentElement.FirstChild;
            string program = Environment.ExpandEnvironmentVariables(node["Command"].InnerText.Trim('"'));
            if (!LocalPath(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            string arguments = node["Arguments"] == null ? "" : Environment.ExpandEnvironmentVariables(node["Arguments"].InnerText);
            return new ProcessStartInfo(program, arguments) { UseShellExecute = false };
        }

        /// <summary>让主窗口确认实际请求进程的会话编号。主窗口未运行或通信中断时放行原程序。</summary>
        private static bool IsManagedChild()
        {
            string name = PipeName(AppDomain.CurrentDomain.BaseDirectory);
            foreach (uint session in Sessions())
                if (AskParent(name + "." + session)) return true;
            return false;
        }

        /// <summary>联系各交互桌面的 SideStage。一个用户同时开多个窗口时，由真正的父会话作出跳过决定。</summary>
        private static bool AskParent(string name)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name,
                    PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    pipe.Connect(250);
                    byte[] decision = new byte[1];
                    var pending = pipe.ReadAsync(decision, 0, 1);
                    return pending.Wait(1200) && pending.Result == 1 && decision[0] == 1;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>枚举本机可见会话，包括断开的父桌面。权限有限时仍能尝试当前桌面和物理控制台。</summary>
        private static HashSet<uint> Sessions()
        {
            var result = new HashSet<uint>();
            using (var current = Process.GetCurrentProcess()) result.Add((uint)current.SessionId);
            result.Add(WTSGetActiveConsoleSessionId());
            IntPtr buffer;
            int count;
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out count))
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var row = (SessionInfo)Marshal.PtrToStructure(IntPtr.Add(buffer, i * Marshal.SizeOf(typeof(SessionInfo))), typeof(SessionInfo));
                        if (row.State <= 5) result.Add(row.Id);
                    }
                }
                finally { WTSFreeMemory(buffer); }
            }
            result.Remove(0);
            result.Remove(uint.MaxValue);
            return result;
        }

        /// <summary>WTS_SESSION_INFO 的本机布局，只使用会话编号和连接状态。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SessionInfo { public uint Id; public IntPtr Name; public int State; }

        /// <summary>取得当前本机控制台会话。</summary>
        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        /// <summary>读取本机的可见会话列表，包括已断开但继续运行的桌面。</summary>
        [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW")]
        private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr buffer, out int count);
        /// <summary>释放会话枚举返回的本机内存。</summary>
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
        /// <summary>辨认 UAC 的已提升令牌，阻止提权任务执行普通用户的备份。</summary>
        [DllImport("advapi32.dll")]
        private static extern bool GetTokenInformation(IntPtr token, int info, out int value, int size, out int written);
#endif
    }
}
