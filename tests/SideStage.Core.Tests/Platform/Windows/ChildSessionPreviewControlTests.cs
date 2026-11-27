/*
 * 文件名称：ChildSessionPreviewControlTests.cs
 * 文件职责：
 *   验证 RDP ActiveX 控件可在当前 Windows 主机上创建并写入子会话预览设置。
 *
 * 测试边界：
 *   - 测试在 STA 线程运行，且只创建本地 COM 控件。
 *   - 只创建并配置 ActiveX，不调用 ConnectPreview，不建立子会话。
 *   - 不启用/禁用系统设置，不断开或注销任何会话。
 */

using System.Reflection;
using System.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.Core.Sessions;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.Core.Tests.Platform.Windows;

/// <summary>
/// 覆盖 RDP ActiveX 预览控件的无连接冒烟路径。
/// </summary>
[TestClass]
public sealed class ChildSessionPreviewControlTests
{
    /// <summary>
    /// 验证控件能够准备 ActiveX 配置，但保持在未连接状态。
    /// </summary>
    [STATestMethod]
    public void PreparePreview_ConfiguresControlWithoutConnecting()
    {
        using ChildSessionPreviewControl control = new();

        control.PreparePreview();

        Assert.AreEqual(
            RdpPreviewConnectionState.Disconnected,
            control.GetConnectionState());
        Assert.AreEqual(ChildSessionAudioMode.DoNotPlay, control.AudioMode);

        object activeX = InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。");
        Assert.AreEqual(
            1920,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "DesktopWidth"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(
            1080,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "DesktopHeight"),
                System.Globalization.CultureInfo.InvariantCulture));
        object advancedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "AdvancedSettings9")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 AdvancedSettings9。");
        Assert.AreEqual(
            true,
            Convert.ToBoolean(
                InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "SmartSizing"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(
            (int)ChildSessionAudioMode.DoNotPlay,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "AudioRedirectionMode"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 验证窗口化预览在发起连接前就已将 Windows 快捷键定向到子会话。
    /// 读取本机 ActiveX 的实际配置，覆盖默认仅在全屏时转发快捷键的回归。
    /// </summary>
    [STATestMethod]
    public void PreparePreview_RoutesWindowsShortcutsToWindowedChildSession()
    {
        using ChildSessionPreviewControl control = new();
        control.PreparePreview();

        object activeX = InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。");
        object securedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "SecuredSettings2")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 SecuredSettings2。");
        object? mode = InvokeStaticPrivateMethod(control, "GetComProperty", securedSettings, "KeyboardHookMode");
        Assert.AreEqual(1, Convert.ToInt32(mode, System.Globalization.CultureInfo.InvariantCulture),
            "窗口化预览获得焦点时，Alt+Tab 和 Win 应交由子会话处理。");
        object advancedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "AdvancedSettings9")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 AdvancedSettings9。");
        object? windowsKey = InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "EnableWindowsKey");
        Assert.IsTrue(Convert.ToBoolean(windowsKey, System.Globalization.CultureInfo.InvariantCulture),
            "子会话应能接收 Windows 键。");
        Assert.AreEqual(RdpPreviewConnectionState.Disconnected, control.GetConnectionState());
    }

    /// <summary>
    /// 验证调用方可显式选择把子会话声音转发到本机，而不发起连接。
    /// </summary>
    [STATestMethod]
    public void PreparePreview_WhenAudioRedirectIsRequested_UsesLocalPlaybackMode()
    {
        using ChildSessionPreviewControl control = new(ChildSessionAudioMode.RedirectToThisComputer);

        control.PreparePreview();

        object activeX = InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。");
        object advancedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "AdvancedSettings9")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 AdvancedSettings9。");
        Assert.AreEqual(
            (int)ChildSessionAudioMode.RedirectToThisComputer,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "AudioRedirectionMode"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 验证未连接时可切换静音策略，并同步更新已经准备好的 RDP ActiveX。
    /// </summary>
    [STATestMethod]
    public void SetAudioMode_WhenDisconnected_UpdatesPreparedControl()
    {
        using ChildSessionPreviewControl control = new();
        control.PreparePreview();

        control.SetAudioMode(ChildSessionAudioMode.RedirectToThisComputer);

        Assert.AreEqual(ChildSessionAudioMode.RedirectToThisComputer, control.AudioMode);
        object activeX = InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。");
        object advancedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "AdvancedSettings9")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 AdvancedSettings9。");
        Assert.AreEqual(
            (int)ChildSessionAudioMode.RedirectToThisComputer,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "AudioRedirectionMode"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 验证断开状态下更改分辨率和 1:1 模式会同步写入 RDP ActiveX，但不发起连接。
    /// </summary>
    [STATestMethod]
    public void SetSettings_WhenDisconnected_UpdatesResolutionAndSmartSizing()
    {
        using ChildSessionPreviewControl control = new();
        control.PreparePreview();
        ChildSessionPreviewSettings settings = new(
            new RemoteDesktopResolution(2560, 1440),
            PreviewScalingMode.OneToOne,
            preserveAspectRatio: false);

        control.SetSettings(settings);

        object activeX = InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。");
        object advancedSettings = InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "AdvancedSettings9")
            ?? throw new AssertFailedException("RDP ActiveX 未提供 AdvancedSettings9。");
        Assert.AreEqual(settings, control.Settings);
        Assert.AreEqual(
            2560,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "DesktopWidth"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(
            1440,
            Convert.ToInt32(
                InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "DesktopHeight"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(
            false,
            Convert.ToBoolean(
                InvokeStaticPrivateMethod(control, "GetComProperty", advancedSettings, "SmartSizing"),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(RdpPreviewConnectionState.Disconnected, control.GetConnectionState());
    }

    /// <summary>
    /// 验证断开状态下请求切换音频模式会立即应用，而不额外要求重连。
    /// </summary>
    [STATestMethod]
    public void RequestAudioModeChange_WhenDisconnected_AppliesModeWithoutReconnect()
    {
        using ChildSessionPreviewControl control = new();
        control.PreparePreview();

        bool reconnectRequired =
            control.RequestAudioModeChange(ChildSessionAudioMode.RedirectToThisComputer);

        Assert.IsFalse(reconnectRequired);
        Assert.AreEqual(ChildSessionAudioMode.RedirectToThisComputer, control.AudioMode);
        Assert.AreEqual(RdpPreviewConnectionState.Disconnected, control.GetConnectionState());
    }

    /// <summary>
    /// 验证系统 RDP ActiveX 接受账户字段、ClearTextPassword 接口及密码清理调用，但不连接。
    /// </summary>
    [STATestMethod]
    public void ConfigureLogonCredentials_SetsIdentityAndCanClearPasswordWithoutConnecting()
    {
        using ChildSessionPreviewControl control = new();
        control.PreparePreview();
        using SecureString password = CreateSecureString("Never-A-Real-Password-42!");
        using WindowsChildSessionCredentials credentials = new("TestUser", "TestDomain", password);

        object activeX = (InvokePrivateMethod(control, "GetActiveXOrThrow")
            ?? throw new AssertFailedException("RDP ActiveX 未创建。"));
        _ = InvokePrivateMethod(control, "ConfigureLogonCredentials", activeX, credentials);

        Assert.AreEqual(
            "TestUser",
            InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "UserName"));
        Assert.AreEqual(
            "TestDomain",
            InvokeStaticPrivateMethod(control, "GetComProperty", activeX, "Domain"));

        _ = InvokePrivateMethod(control, "TryClearRdpPassword");
        FieldInfo passwordState = typeof(ChildSessionPreviewControl).GetField(
            "_rdpPasswordWasSet",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("RDP 密码状态字段不存在。");
        Assert.IsFalse((bool)(passwordState.GetValue(control) ?? true));
        Assert.AreEqual(RdpPreviewConnectionState.Disconnected, control.GetConnectionState());
    }

    /// <summary>
    /// 验证系统未启用子会话时，连接流程在创建 ActiveX 和发起连接前明确失败。
    /// </summary>
    [STATestMethod]
    public void ConnectPreview_WhenChildSessionsAreDisabled_FailsBeforeCreatingControl()
    {
        FakeSessionInspector inspector = new(childSessionsEnabled: false);
        using ChildSessionPreviewControl control = new(() => inspector);

        Assert.ThrowsExactly<InvalidOperationException>(() => control.ConnectPreview());
        Assert.IsFalse(control.IsHandleCreated);
    }

    /// <summary>
    /// 验证未知音频模式在创建 COM 控件前明确拒绝。
    /// </summary>
    [STATestMethod]
    public void Constructor_WhenAudioModeIsUnknown_ThrowsArgumentOutOfRangeException()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ChildSessionPreviewControl((ChildSessionAudioMode)1));
    }

    /// <summary>
    /// 为本机 COM 冒烟测试构造 SecureString。
    /// </summary>
    /// <param name="value">不可用于真实账户的测试密码。</param>
    /// <returns>只读的受保护字符串。</returns>
    private static SecureString CreateSecureString(string value)
    {
        SecureString result = new();
        foreach (char character in value)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    /// <summary>
    /// 通过反射调用仅供当前类内部使用的实例级 COM 辅助方法。
    /// </summary>
    /// <param name="target">辅助方法所属实例。</param>
    /// <param name="methodName">私有方法名称。</param>
    /// <param name="arguments">按方法签名排列的参数。</param>
    /// <returns>方法返回值。</returns>
    private static object? InvokePrivateMethod(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"测试辅助方法 {methodName} 不存在。");
        return method.Invoke(target, arguments);
    }

    /// <summary>
    /// 通过反射调用仅供控件内部使用的静态 COM 辅助方法。
    /// </summary>
    /// <param name="target">声明静态辅助方法的控件实例。</param>
    /// <param name="methodName">私有静态方法名称。</param>
    /// <param name="arguments">按方法签名排列的参数。</param>
    /// <returns>方法返回值。</returns>
    private static object? InvokeStaticPrivateMethod(
        object target,
        string methodName,
        params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"测试辅助方法 {methodName} 不存在。");
        return method.Invoke(null, arguments);
    }

    /// <summary>
    /// 为控件连接保护测试提供确定性的只读会话观察结果。
    /// </summary>
    private sealed class FakeSessionInspector(bool childSessionsEnabled) : ISessionInspector
    {
        /// <summary>
        /// 返回模拟的当前父会话快照。
        /// </summary>
        public SessionSnapshot GetCurrentSession() => new(
            new SessionId(10),
            SessionConnectionState.Active,
            IsChildSession: false,
            DateTimeOffset.UtcNow);

        /// <summary>
        /// 返回模拟的当前子会话；本测试不应调用此方法。
        /// </summary>
        public SessionSnapshot? TryGetChildSession() => null;

        /// <summary>
        /// 返回测试初始化时指定的子会话能力开关。
        /// </summary>
        public bool IsChildSessionsEnabled() => childSessionsEnabled;
    }
}
