/*
 * 文件职责：管理一个预览窗口的活动日志分卷，并为所有窗口回收历史日志。
 * 兼容范围：旧版文件和新分卷沿用同一命名规则。只检查指定目录的直接子文件。
 * 调用约定：窗口在自己的 UI 线程写入、维护和释放实例。跨进程保护由 Windows 文件共享模式提供。
 */
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SideStage.App;

/// <summary>按时间、容量和数量限制本机活动日志，为正在写入的分卷保留只读句柄。</summary>
internal sealed class ActivityLogStore(string directory) : IDisposable
{
    internal const int RetentionDays = 14, MaxFiles = 128, VisibleEntries = 500;
    internal const int SegmentBytes = 2 * 1024 * 1024, TotalBytes = 32 * 1024 * 1024;
    private static readonly Regex OwnedName = new(@"\Apreview-[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{32}\.log\z");
    private FileStream? _lease;
    private DateTime _segmentDay;

    /// <summary>以当前桌面用户的权限追加一条 UTF-8 记录。按实际字节数或日期切换分卷。</summary>
    internal void Append(string line, DateTime utcNow)
    {
        byte[] record = EncodeLine(line);
        StartupUserAccess.AsUser(() =>
        {
            if (_lease == null || _segmentDay != utcNow.Date || _lease.Length + record.Length > SegmentBytes)
                StartSegment(utcNow);
            using var output = new FileStream(_lease!.Name, FileMode.Append, FileAccess.Write, FileShare.Read);
            output.Write(record);
            return true;
        });
    }

    /// <summary>仅当单条记录本身超过整卷时截短正文，保留完整的 UTF-8 字符、截断说明和行尾。</summary>
    private static byte[] EncodeLine(string line)
    {
        string end = Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(line) <= SegmentBytes - end.Length)
            return Encoding.UTF8.GetBytes(line + end);

        byte[] bytes = new byte[SegmentBytes];
        byte[] suffix = Encoding.UTF8.GetBytes("…（单条日志过长，已截断）" + end);
        Encoding.UTF8.GetEncoder().Convert(line.AsSpan(), bytes.AsSpan(0, bytes.Length - suffix.Length),
            true, out _, out int written, out _);
        suffix.CopyTo(bytes, written);
        return bytes[..(written + suffix.Length)];
    }

    /// <summary>独占新文件名并在创建句柄关闭前取得读租约。其他窗口无法删除此卷，普通文本查看器仍可打开它。</summary>
    private void StartSegment(DateTime utcNow)
    {
        Dispose();
        var root = Directory.CreateDirectory(directory);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("日志目录是文件系统链接。");
        string stamp = utcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(root.FullName, $"preview-{stamp}-{Guid.NewGuid():N}.log");
        using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            _lease = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _segmentDay = utcNow.Date;
        Prune(utcNow);
    }

    /// <summary>窗口长期开着且没有新事件时也释放昨天的卷，并重新检查过期文件。</summary>
    internal void Maintain(DateTime utcNow)
    {
        if (_segmentDay != utcNow.Date) Dispose();
        try { StartupUserAccess.AsUser(() => { Prune(utcNow); return true; }); }
        catch (Exception ex) when (IsFileError(ex)) { /* 权限暂不可用时留待下次维护。 */ }
    }

    /// <summary>按最早写入优先回收。当前卷按满卷预留容量，平时追加无需重新扫描目录。</summary>
    private void Prune(DateTime utcNow)
    {
        try
        {
            var root = new DirectoryInfo(directory);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var files = new List<(FileInfo File, DateTime Written, long Size)>();
            foreach (var file in root.EnumerateFiles("preview-*.log"))
            {
                if (!OwnedName.IsMatch(file.Name)) continue;
                try
                {
                    if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        files.Add((file, file.LastWriteTimeUtc, file.Length));
                }
                catch (Exception ex) when (IsFileError(ex)) { /* 文件可能刚被另一个窗口清理。 */ }
            }
            long total = files.Sum(entry => entry.File.FullName == _lease?.Name ? SegmentBytes : entry.Size);
            int count = files.Count;
            DateTime cutoff = utcNow.AddDays(-RetentionDays);
            foreach (var entry in files.OrderBy(entry => entry.Written))
            {
                if (entry.Written > cutoff && count <= MaxFiles && total <= TotalBytes) break;
                if (entry.File.FullName == _lease?.Name) continue;
                try
                {
                    entry.File.Delete();
                    total -= entry.Size;
                    count--;
                }
                catch (Exception ex) when (IsFileError(ex)) { /* 正在使用的分卷留给下次清理。 */ }
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { /* 清理失败仍允许写入本次活动。 */ }
    }

    /// <summary>归类个人目录、文件共享及普通用户令牌的可恢复错误。</summary>
    internal static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException;

    /// <summary>关闭当前分卷的读租约，使其他窗口可以正常回收它。</summary>
    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
