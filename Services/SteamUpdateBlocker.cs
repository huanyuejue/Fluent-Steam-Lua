using System;
using System.IO;
using System.Text;

namespace SteamLuaManager.Services;

/// <summary>
/// 通过 Steam 根目录下的 steam.cfg 关闭客户端自动更新。
///
/// 这个文件是 Steam 启动器（BootStrapper）的开关，写入后启动器不再检查更新，
/// 客户端版本停留在当前版本，避免更新后内核注入失效需要重新适配。
/// 文件需要设为只读：Steam 检测到配置后会尝试回写内容，可写状态下会被改回去。
/// </summary>
public interface ISteamUpdateBlocker
{
    /// <summary>当前是否已生效（文件存在且内容正确）。</summary>
    bool IsEnabled { get; }

    /// <summary>写入 steam.cfg 并设为只读。</summary>
    bool Enable();

    /// <summary>删除 steam.cfg，恢复 Steam 自动更新。</summary>
    bool Disable();

    /// <summary>上一次操作失败的原因，成功时为 null。</summary>
    string? FailReason { get; }
}

public sealed class SteamUpdateBlocker : ISteamUpdateBlocker
{
    private const string ConfigFileName = "steam.cfg";

    // 启动器读取的两个开关：前者停用所有自更新动作，后者明确禁用启动器自身更新
    private const string ConfigContent =
        "BootStrapperInhibitAll=Enable\r\n" +
        "BootStrapperForceSelfUpdate=disable\r\n";

    private readonly ISteamPathService _steamPath;

    public SteamUpdateBlocker(ISteamPathService steamPath)
    {
        _steamPath = steamPath;
    }

    public bool IsEnabled
    {
        get
        {
            var file = GetConfigPath();
            if (file == null || !File.Exists(file)) return false;

            // 与写入后的校验用同一套判断，避免两处标准不一致导致反复重写
            return IsContentCorrect(file);
        }
    }

    public bool Enable()
    {
        var file = GetConfigPath();
        if (file == null)
        {
            LogService.Warn(L.Get("LogCat_Settings"), "未找到 Steam 目录，无法写入 steam.cfg");
            return false;
        }

        try
        {
            // 已有内容正确就直接补只读位：文件可能被 Steam 改回可写，
            // 这时重写一次纯属多余，还多一次被占用的机会
            if (File.Exists(file) && IsContentCorrect(file))
            {
                EnsureReadOnly(file);
                FailReason = null;
                return true;
            }

            // 内容不对或被 Steam 改写过：先清只读，再覆盖
            if (File.Exists(file))
            {
                EnsureWritable(file);
                File.Delete(file);
            }

            File.WriteAllText(file, ConfigContent, new UTF8Encoding(false));

            // 只读是关键：可写状态下 Steam 启动时会把自己的默认值写回来
            EnsureReadOnly(file);

            FailReason = null;
            LogService.Info(L.Get("LogCat_Settings"), "已写入 steam.cfg 禁用 Steam 自动更新");
            return true;
        }
        catch (Exception ex)
        {
            FailReason = DescribeError(ex);
            LogService.Warn(L.Get("LogCat_Settings"), $"写入 steam.cfg 失败{DescribeError(ex)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>失败原因的可读描述，供界面提示用。</summary>
    public string? FailReason { get; private set; }

    /// <summary>把常见失败翻译成可操作的提示，纯英文异常信息对用户没有意义。</summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => L.Get("Settings_DisableSteamUpdateNeedAdmin"),
        IOException => L.Get("Settings_DisableSteamUpdateBusy"),
        _ => L.Get("Settings_DisableSteamUpdateFail")
    };

    /// <summary>判断文件内容是否已是目标内容，忽略大小写与换行差异。</summary>
    private static bool IsContentCorrect(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            return text.Contains("BootStrapperInhibitAll=Enable", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("BootStrapperForceSelfUpdate=disable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>去掉只读标记，保留归档等其它属性。</summary>
    private static void EnsureWritable(string file)
    {
        try
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // 属性读写失败交给后续写入暴露真实原因
        }
    }

    /// <summary>加上只读标记，阻止 Steam 回写。</summary>
    private static void EnsureReadOnly(string file)
    {
        try
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) == 0)
            {
                File.SetAttributes(file, attrs | FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // 只读失败不影响文件已写入，功能仍然生效
        }
    }

    public bool Disable()
    {
        var file = GetConfigPath();
        if (file == null || !File.Exists(file))
        {
            FailReason = null;
            return true;
        }

        try
        {
            // 只读文件删不掉，先还原属性；与写入路径共用同一套处理
            EnsureWritable(file);
            File.Delete(file);

            FailReason = null;
            LogService.Info(L.Get("LogCat_Settings"), "已删除 steam.cfg，恢复 Steam 自动更新");
            return true;
        }
        catch (Exception ex)
        {
            FailReason = DescribeError(ex);
            LogService.Warn(L.Get("LogCat_Settings"), $"删除 steam.cfg 失败{DescribeError(ex)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>steam.cfg 的完整路径；拿不到 Steam 目录时返回 null。</summary>
    private string? GetConfigPath()
    {
        try
        {
            var root = _steamPath.DetectSteamPath();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

            return Path.Combine(root, ConfigFileName);
        }
        catch
        {
            return null;
        }
    }
}
