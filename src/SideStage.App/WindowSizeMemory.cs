/*
 * 文件名称：WindowSizeMemory.cs
 * 文件职责：在主窗口首次显示前恢复尺寸，窗口确认关闭后把尺寸保存到当前用户的注册表。
 *
 * 保存约定：
 *   - 一次写入普通窗口尺寸、保存时的 DPI 和最大化状态，保证读到的是同一次关闭的数据。
 *   - 最小化时沿用之前的显示状态；最大化时同时保留可还原的窗口大小。
 *   - 还原尺寸按启动显示器的 DPI 和可用区域换算，窗口沿用居中显示。
 */
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace SideStage.App;

/// <summary>管理桌面宿主的窗口偏好，不依赖 RDP 连接或子会话状态。</summary>
internal static class WindowSizeMemory
{
    /// <summary>登记一次加载和关闭事件。仅在真正关闭时写入，取消关闭可继续调整窗口。</summary>
    internal static void Attach(Form window, string keyPath, string valueName)
    {
        bool wasMaximized = false;
        window.Load += (_, _) =>
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key?.GetValue(valueName) is not string json ||
                    JsonSerializer.Deserialize<SavedSize>(json) is not { Width: > 0, Height: > 0, Dpi: > 0 } saved)
                    return;

                // Load 时窗口已有句柄，DeviceDpi 和工作区来自实际启动显示器。
                Rectangle work = Screen.FromHandle(window.Handle).WorkingArea;
                Size size = saved.Fit(window.DeviceDpi, window.MinimumSize, work.Size);
                window.Bounds = new Rectangle(work.X + Math.Max(0, work.Width - size.Width) / 2,
                    work.Y + Math.Max(0, work.Height - size.Height) / 2, size.Width, size.Height);
                if (saved.Maximized) window.WindowState = FormWindowState.Maximized;
            }
            catch (Exception ex) when (ex is JsonException or UnauthorizedAccessException or SecurityException or IOException)
            {
                // 用户偏好缺失、损坏或不可读取时，使用窗体原有的启动布局。
            }
        };
        window.Resize += (_, _) =>
        {
            if (window.WindowState != FormWindowState.Minimized)
                wasMaximized = window.WindowState == FormWindowState.Maximized;
        };
        window.FormClosed += (_, _) =>
        {
            Size normal = window.WindowState == FormWindowState.Normal ? window.Size : window.RestoreBounds.Size;
            if (normal.Width <= 0 || normal.Height <= 0) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                var saved = new SavedSize(normal.Width, normal.Height, window.DeviceDpi, wasMaximized);
                key.SetValue(valueName, JsonSerializer.Serialize(saved), RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // 保存窗口偏好失败不应阻止关闭，也不影响上一次成功写入的值。
            }
        };
    }

    /// <summary>记录普通窗口的物理像素和对应 DPI，避免同一缩放比例下重复开关造成舍入漂移。</summary>
    internal sealed record SavedSize(int Width, int Height, int Dpi, bool Maximized)
    {
        /// <summary>把原尺寸换算到当前 DPI，在工作区容许的范围内保留窗体的最小尺寸。</summary>
        internal Size Fit(int currentDpi, Size minimum, Size workArea)
        {
            int Scale(int value, int min, int available)
            {
                double pixels = Math.Round((double)value * currentDpi / Dpi);
                return (int)Math.Clamp(pixels, min, Math.Max(min, available));
            }
            return new Size(Scale(Width, minimum.Width, workArea.Width), Scale(Height, minimum.Height, workArea.Height));
        }
    }
}
