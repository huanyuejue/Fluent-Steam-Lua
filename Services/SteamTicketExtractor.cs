using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

/// <summary>
/// 提取调度器（主进程侧）：启动 --ticket-worker 子进程执行 steamclient 原生提取，
/// 通过临时 JSON 文件回收票据（base64），并原子落盘到 cache\denuvo\{appid}\tickets.txt。
/// </summary>
public sealed class SteamTicketExtractor
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(45);
    private static readonly Regex AcfNameRegex = new(
        @"^\s*""name""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;

    // appinfo.vdf 解析结果缓存（按文件最后写入时间失效，避免每次提取重复解析）
    private string? _cachedAppInfoPath;
    private DateTime _cachedAppInfoTime;
    private Dictionary<uint, string>? _cachedAppInfoNames;

    public SteamTicketExtractor(ISteamPathService steamPathService, ISettingsService settingsService)
    {
        _steamPathService = steamPathService;
        _settingsService = settingsService;
    }

    public string GetDefaultTicketsPath(uint appId)
        => Path.Combine(AppContext.BaseDirectory, "cache", "denuvo",
            appId.ToString(CultureInfo.InvariantCulture), "tickets.txt");

    public async Task<TicketExtractionResult> ExtractAsync(uint appId, CancellationToken ct = default)
    {
        if (!Environment.Is64BitProcess)
            throw new InvalidOperationException("提取授权需要 64 位进程");

        if (!IsSteamRunning())
            throw new InvalidOperationException("请先启动 Steam 并登录拥有该游戏的账号");

        var resultFile = Path.Combine(Path.GetTempPath(), $"steam_lua_ticket_{appId}_{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "SteamLuaManager.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--ticket-worker");
            psi.ArgumentList.Add(appId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(resultFile);
            psi.ArgumentList.Add(_settingsService.Load().EnableLogging ? "1" : "0");

            process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动提取子进程");

            await process.WaitForExitAsync(ct).WaitAsync(WorkerTimeout, ct);
            var exitCode = process.ExitCode;
            process = null; // 正常退出，无需清理

            LogService.Info("提取",
                $"提取子进程退出码 {exitCode}，结果文件存在={File.Exists(resultFile)}：{resultFile}");

            if (!File.Exists(resultFile))
                throw new InvalidOperationException(
                    $"提取子进程异常退出（退出码 {exitCode}），未生成结果文件；请查看软件目录 app.log 中 [提取] 分类的 worker 日志");

            TicketWorkerResult? result;
            try
            {
                result = JsonSerializer.Deserialize<TicketWorkerResult>(
                    await File.ReadAllTextAsync(resultFile, ct));
            }
            catch (JsonException ex)
            {
                LogService.Error("提取", $"提取子进程返回的结果损坏: {ex.Message}");
                throw new InvalidOperationException("提取子进程返回的结果损坏");
            }

            if (result == null)
                throw new InvalidOperationException("提取子进程返回空结果");
            if (!result.Ok)
                throw new InvalidOperationException(result.ErrorMessage ?? "提取授权失败");
            if (string.IsNullOrEmpty(result.AppTicketBase64))
                throw new InvalidOperationException("提取结果缺少所有权票据");
            if (string.IsNullOrEmpty(result.ETicketBase64))
                throw new InvalidOperationException("提取结果缺少加密票据");

            var appTicket = Convert.FromBase64String(result.AppTicketBase64);
            var eticket = Convert.FromBase64String(result.ETicketBase64);
            if (appTicket.Length < 20)
            {
                Array.Clear(appTicket);
                throw new InvalidOperationException("提取的所有权票据无效（过短）");
            }

            var steamId = BitConverter.ToUInt64(appTicket, 8);
            var gameName = GetLocalGameName(appId);
            var outputPath = WriteTicketsFile(appId, gameName, appTicket, eticket);

            var ticket = new TicketData(appId, steamId, appTicket, eticket, outputPath);
            Array.Clear(appTicket);
            Array.Clear(eticket);
            return new TicketExtractionResult(ticket, outputPath, null);
        }
        catch (TimeoutException)
        {
            LogService.Error("提取", "提取子进程超时（45 秒），已终止");
            throw new InvalidOperationException("提取授权超时（45 秒），请确认 Steam 在线后重试");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(); } catch { }
            }
            try { File.Delete(resultFile); } catch { }
        }
    }

    private static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本地获取游戏名（纯离线）：优先 appmanifest_acf，兜底解析 appinfo.vdf（中文名优先）。</summary>
    private string? GetLocalGameName(uint appId)
    {
        try
        {
            var acfPath = _steamPathService.FindAppManifest((int)appId);
            if (acfPath != null)
            {
                var name = ReadAcfName(acfPath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    LogService.Info("提取", $"已获取游戏名称（appmanifest）：{name}");
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("提取", $"读取 appmanifest 游戏名失败: {ex.Message}");
        }

        try
        {
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return null;

            var appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");
            if (!File.Exists(appInfoPath)) return null;

            var names = LoadAppInfoNames(appInfoPath);
            if (names != null && names.TryGetValue(appId, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                LogService.Info("提取", $"已获取游戏名称（appinfo.vdf）：{name}");
                return name;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("提取", $"解析 appinfo.vdf 游戏名失败: {ex.Message}");
        }
        return null;
    }

    private static string? ReadAcfName(string acfPath)
    {
        foreach (var line in File.ReadAllLines(acfPath))
        {
            var match = AcfNameRegex.Match(line.Trim());
            if (match.Success)
                return match.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return null;
    }

    private Dictionary<uint, string>? LoadAppInfoNames(string appInfoPath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(appInfoPath);
        if (_cachedAppInfoNames != null &&
            _cachedAppInfoPath == appInfoPath &&
            _cachedAppInfoTime == lastWrite)
        {
            return _cachedAppInfoNames;
        }

        var entries = AppInfoVdf.Parse(appInfoPath);
        var names = new Dictionary<uint, string>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Name != null)
                names[entry.AppId] = entry.Name;
        }

        _cachedAppInfoPath = appInfoPath;
        _cachedAppInfoTime = lastWrite;
        _cachedAppInfoNames = names;
        return names;
    }

    private static string WriteTicketsFile(uint appId, string? gameName, byte[] appTicket, byte[] eticket)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "cache", "denuvo",
            appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "tickets.txt");
        var now = DateTime.Now;
        var content = new StringBuilder()
            .Append("appid:").Append(appId.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("appticket(").Append(appTicket.Length).Append("bytes):").Append(Hex(appTicket)).Append('\n')
            .Append("eticket(").Append(eticket.Length).Append("bytes):").Append(Hex(eticket)).Append('\n')
            .Append('\n');
        if (!string.IsNullOrEmpty(gameName))
            content.Append("# 游戏名称：").Append(gameName).Append('\n');
        content
            .Append("# 提取时间：").Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n')
            .Append("# 失效时间：").Append(now.AddMinutes(30).ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("（授权有效期 30 分钟，过期后需重新提取）").Append('\n');
        var text = content.ToString();

        var tmp = Path.Combine(dir, $".tickets.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { }

        LogService.Info("提取", $"授权已保存到 {path}（AppTicket {appTicket.Length}B / ETicket {eticket.Length}B）");
        return path;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
