/*
 * 文件名称：TheatreTypography.cs
 * 文件职责：为纸面预览和凭据窗口加载随程序分发的 HarmonyOS Sans SC 常规与粗体。
 * 生命周期：字体供当前进程中的窗口共用。GDI 和 GDI+ 分别注册，以兼容原生控件和自绘文字。
 * 分发来源：OpenHarmony resources，字体文件保留原样，授权声明见 Artwork/Fonts/LICENSE_Fonts.txt。
 */
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SideStage.App;

internal static class TheatreTypography
{
    private static readonly PrivateFontCollection Fonts = new();
    private static readonly FontFamily Family;

    /// <summary>仅向本进程注册字体文件。使用实际加载的字族，避免系统缺字库时悄悄替换中文字体。</summary>
    static TheatreTypography()
    {
        foreach (string weight in new[] { "Regular", "Bold" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Artwork", "Fonts", $"HarmonyOS_Sans_SC_{weight}.ttf");
            if (AddFontResourceEx(path, 0x10, 0) == 0)
                throw new InvalidDataException($"无法加载预览字体：{Path.GetFileName(path)}");
            Fonts.AddFontFile(path);
        }
        Family = Fonts.Families.Single();
    }

    /// <summary>按控件的字号和字重创建字体实例，由拥有此实例的窗体释放。</summary>
    internal static Font Create(float size, FontStyle style = FontStyle.Regular) =>
        new(Family, size, style, GraphicsUnit.Point);

    /// <summary>为使用 GDI 的原生按钮、标签和文本框提供进程私有字体，退出后由系统卸载。</summary>
    [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string file, uint flags, nint reserved);
}
