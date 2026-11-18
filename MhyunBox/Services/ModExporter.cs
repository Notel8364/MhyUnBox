using System;
using System.IO;
using System.Linq;
using MhyunBox.Settings;

namespace MhyunBox.Services;

/// <summary>
/// MythosNexus 模组随程序内置在 Mods 目录，允许用户自行复制到桌面后手动放进任意客户端，
/// 不做主动注入。首次启动时自动复制一次。
/// </summary>
public static class ModExporter
{
    public const string FilePrefix = "Mythos-Nexus";

    public static string? FindBundledJar()
    {
        try
        {
            string mods = ModInjectService.ModsDirectory();
            if (!Directory.Exists(mods)) return null;
            return Directory.GetFiles(mods, FilePrefix + "*.jar", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把内置模组复制到桌面，返回可直接展示的结果文案。</summary>
    public static string CopyToDesktop(bool overwrite)
    {
        string? source = FindBundledJar();
        if (source == null)
        {
            return "未找到内置模组（程序 Mods 目录缺少 " + FilePrefix + "-*.jar）";
        }
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop))
            {
                return "无法获取桌面路径";
            }
            string target = Path.Combine(desktop, Path.GetFileName(source));
            if (!overwrite && File.Exists(target))
            {
                return "桌面上已有 " + Path.GetFileName(target);
            }
            File.Copy(source, target, overwrite: true);
            LogHelper.Log("[模组] 已复制到桌面：" + target);
            return "已复制到桌面：" + Path.GetFileName(target);
        }
        catch (Exception ex)
        {
            LogHelper.LogError(ex, "[模组] 复制到桌面失败");
            return "复制失败：" + ex.Message;
        }
    }

    /// <summary>首次启动时自动复制一次；之后由用户点按钮手动复制。</summary>
    public static void EnsureCopiedOnFirstRun()
    {
        try
        {
            AppSettings settings = SettingsManager.Instance;
            if (settings.ModCopiedToDesktop) return;
            string result = CopyToDesktop(overwrite: false);
            settings.ModCopiedToDesktop = true;
            SettingsManager.Save(settings);
            LogHelper.Log("[模组] 首次启动自动复制：" + result);
        }
        catch (Exception ex)
        {
            LogHelper.LogError(ex, "[模组] 首次启动自动复制失败");
        }
    }
}
