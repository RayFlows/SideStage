/*
 * 文件职责：为桌面应用记住当前 Windows 用户选定的子会话画面和声音设置。
 * 保存时机：用户的修改被预览控件接受后立即写入。每个窗口只写自己改过的选项，
 * 避免另一个窗口使用较早的设置覆盖新选择。自定义模式和宽高作为一个值保存。
 */
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App;

/// <summary>窗口创建时读取一次用户偏好。预览控件仍然只接收普通参数，可由其他宿主独立使用。</summary>
internal sealed class PreviewPreferences
{
    private readonly string _keyPath;
    private sealed record SavedResolution(int Width, int Height, bool Custom);

    internal ChildSessionPreviewSettings Display { get; private set; } = ChildSessionPreviewSettings.Default;
    internal bool CustomResolution { get; private set; }
    internal ChildSessionAudioMode Audio { get; private set; } = ChildSessionAudioMode.DoNotPlay;

    /// <summary>从当前用户的应用设置恢复已知值。旧版本没有记录的项目，以及损坏的单项，使用原默认值。</summary>
    internal PreviewPreferences(string keyPath)
    {
        _keyPath = keyPath;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null) return;
            var resolution = Display.Resolution;
            if (key.GetValue("PreviewResolution") is string json)
            {
                try
                {
                    if (JsonSerializer.Deserialize<SavedResolution>(json) is
                        { Width: >= 200 and <= 8192, Height: >= 200 and <= 8192 } saved)
                    {
                        resolution = new(saved.Width, saved.Height);
                        CustomResolution = saved.Custom;
                    }
                }
                catch (JsonException) { /* 分辨率记录损坏时仍继续恢复声音和缩放。 */ }
            }
            var scaling = key.GetValue("PreviewScaling") is int mode && Enum.IsDefined((PreviewScalingMode)mode)
                ? (PreviewScalingMode)mode : Display.ScalingMode;
            Display = new(resolution, scaling, key.GetValue("PreviewKeepAspect") is not 0);
            if (key.GetValue("PreviewMuted") is 0) Audio = ChildSessionAudioMode.RedirectToThisComputer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 用户配置暂时不可读时仍允许正常打开预览窗口。
        }
    }

    /// <summary>保存本窗口实际变动的画面选项。自定义宽高即使恰好等于预设，也会恢复为可编辑模式。</summary>
    internal bool SaveDisplay(ChildSessionPreviewSettings settings, bool custom)
    {
        if (Display == settings && CustomResolution == custom) return true;
        if (!TryWrite(key =>
        {
            if (Display.Resolution != settings.Resolution || CustomResolution != custom)
                key.SetValue("PreviewResolution", JsonSerializer.Serialize(
                    new SavedResolution(settings.Resolution.Width, settings.Resolution.Height, custom)));
            if (Display.ScalingMode != settings.ScalingMode)
                key.SetValue("PreviewScaling", (int)settings.ScalingMode);
            if (Display.PreserveAspectRatio != settings.PreserveAspectRatio)
                key.SetValue("PreviewKeepAspect", settings.PreserveAspectRatio ? 1 : 0);
        })) return false;
        Display = settings;
        CustomResolution = custom;
        return true;
    }

    /// <summary>保存已接受的静音选择，供下次创建预览时使用。</summary>
    internal bool SaveAudio(ChildSessionAudioMode audio)
    {
        if (Audio == audio) return true;
        if (!TryWrite(key => key.SetValue("PreviewMuted", audio == ChildSessionAudioMode.DoNotPlay ? 1 : 0)))
            return false;
        Audio = audio;
        return true;
    }

    /// <summary>写入当前账户的注册表。失败时让调用方提示用户，仍保留本次已经生效的设置。</summary>
    private bool TryWrite(Action<RegistryKey> write)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
            write(key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
