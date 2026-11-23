/*
 * 文件名称：WindowsEdition.cs
 * 文件职责：读取本机 Windows 的本地化名称和版本标识，为首页选择兼容性提示。
 * 判断边界：绿色标识对照微软 Remote Desktop 主机版本列表，不表示子会话已经连通；
 *   家庭版和无法确认的版本都保留尝试连接的机会。判断不依赖系统显示语言。
 */
using System.Management;
using Microsoft.Win32;

namespace SideStage.App;

internal enum EditionMark { Unknown, ListedHost, Home }

internal sealed record WindowsEdition(string Name, EditionMark Mark)
{
    /// <summary>一次查询取得名称、产品 SKU 与系统版本。服务器另核实是否安装桌面体验。</summary>
    internal static WindowsEdition? Read()
    {
        using ManagementObjectSearcher query = new(@"root\CIMV2",
            "SELECT Caption, OperatingSystemSKU, ProductType, Version FROM Win32_OperatingSystem");
        query.Options.Timeout = TimeSpan.FromSeconds(4);
        using ManagementObjectCollection rows = query.Get();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                string? name = (row["Caption"] as string)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                const string vendor = "Microsoft ";
                if (name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) name = name[vendor.Length..];
                uint? type = row["ProductType"] as uint?;
                bool desktopServer = type is 2 or 3 && Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "InstallationType", null) as string == "Server";
                Version.TryParse(row["Version"] as string, out Version? version);
                return new(name, Classify(version, type, row["OperatingSystemSKU"] as uint?, desktopServer));
            }
        }
        return null;
    }

    /// <summary>按 Win32_OperatingSystem 的数字字段匹配 Windows 10/11 桌面系列及现代桌面体验服务器。
    /// 未列入的产品不猜测，尤其不能把其他 Core 产品误当作家庭版。</summary>
    internal static EditionMark Classify(Version? version, uint? type, uint? sku, bool desktopServer)
    {
        if (version is not { Major: 10, Minor: 0 }) return EditionMark.Unknown;
        if (type is 2 or 3) return desktopServer ? EditionMark.ListedHost : EditionMark.Unknown;
        if (type != 1) return EditionMark.Unknown;
        return sku switch
        {
            // Home N、中国版、单语言版、标准家庭版。
            0x62 or 0x63 or 0x64 or 0x65 => EditionMark.Home,
            // 专业版、工作站专业版、专业教育版及相应的 N 版。
            0x30 or 0x31 or 0xA1 or 0xA2 or 0xA4 or 0xA5 => EditionMark.ListedHost,
            // 教育版；企业版、评估版、长期服务版以及对应的 N 版。
            0x79 or 0x7A or 0x04 or 0x1B or 0x48 or 0x54 or 0x7D or 0x7E or 0x81 or 0x82 => EditionMark.ListedHost,
            _ => EditionMark.Unknown,
        };
    }
}
