using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SteamAutoCrack.Core.Utils;
using SacConfig = SteamAutoCrack.Core.Config.Config;

namespace SteamLuaManager.Services;

// 免启动破解桥接：进程内调用 vendored Core（CrackTool/，已改 net8/x64）。
// 两件必须事：路径重定向（对方默认全写程序目录）+ Serilog 日志桥（否则进度黑盒）。
// 破解操作串行（Core 全是静态 Config），单飞行门控。
public sealed class CrackToolService : ICrackToolService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private bool _initialized;
    private bool _disposed;
    private volatile bool _runHadError;
    private IProgress<string>? _activeProgress;
    private readonly ISettingsService _settingsService;

    public CrackToolService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string CacheRoot => Path.Combine(AppContext.BaseDirectory, "cache", "cracktool");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    public async Task<bool> CrackAsync(string inputPath, string appId, string? webApiKey, IProgress<string>? log, CancellationToken ct = default, bool unpackOnly = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureInitialized(log);
            _runHadError = false;
            SacConfig.InputPath = inputPath;
            if (!string.IsNullOrWhiteSpace(appId))
                SacConfig.EMUGameInfoConfigs.AppID = appId.Trim();
            SacConfig.EMUGameInfoConfigs.SteamWebAPIKey = webApiKey?.Trim() ?? "";
            // 信息源锁定 SteamKit2 Client（不走 Steam Web API，免 Key 也能跑）
            SacConfig.EMUGameInfoConfigs.GameInfoAPI = EMUGameInfoConfig.GeneratorGameInfoAPI.GeneratorSteamClient;
            var p = SacConfig.ProcessConfigs;
            if (unpackOnly)
            {
                // 仅脱壳：Steamless 去壳，原 exe 备成 .exe.bak；steam_api* 不动，照常要开 Steam
                p.GenerateEMUGameInfo = false;
                p.GenerateEMUConfig = false;
                p.Unpack = true;
                p.ApplyEMU = false;
                p.GenerateCrackOnly = false;
                p.Restore = false;
            }
            else
            {
                p.GenerateEMUGameInfo = true;
                p.GenerateEMUConfig = true;
                p.Unpack = true;
                p.ApplyEMU = true;
                p.GenerateCrackOnly = false;
                p.Restore = false;
            }
            await new Processor().ProcessFileGUI(ct).ConfigureAwait(false);
            return !_runHadError && !ct.IsCancellationRequested;
        }
        finally
        {
            // 先断开进度再放行：否则后一个运行的日志会串进前一个的界面
            _activeProgress = null;
            _gate.Release();
        }
    }

    public async Task<bool> RestoreAsync(string inputPath, IProgress<string>? log, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureInitialized(log);
            _runHadError = false;
            SacConfig.InputPath = inputPath;
            var p = SacConfig.ProcessConfigs;
            p.GenerateEMUGameInfo = false;
            p.GenerateEMUConfig = false;
            p.Unpack = false;
            p.ApplyEMU = false;
            p.GenerateCrackOnly = false;
            p.Restore = true;
            await new Processor().ProcessFileGUI(ct).ConfigureAwait(false);
            return !_runHadError && !ct.IsCancellationRequested;
        }
        finally
        {
            _activeProgress = null;
            _gate.Release();
        }
    }

    public async Task<bool> UpdateEmuAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureInitialized(log);
            _runHadError = false;
            var updater = new EMUUpdater();
            // 跟随接口设置的镜像偏好组装候选源（直连/镜像逐个试）；用户要求见日志用哪条
            string? mirror;
            try { mirror = _settingsService.Load().ManifestMirror; }
            catch { mirror = null; }
            updater.UrlCandidates = url => GitHubMirror.OrderSources(url, mirror);
            await updater.Init().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // EMUUpdater 自带成功/失败日志与返回值；它不认 ct，收尾后这里补检查
            var ok = await updater.Download(false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (ok) DeleteEmuArchive();
            return ok && !_runHadError && !ct.IsCancellationRequested;
        }
        finally
        {
            _activeProgress = null;
            _gate.Release();
        }
    }

    // emu 是否已就绪：照抄上游部署校验口径（两个关键文件都在才算）
    public bool IsEmuInstalled()
    {
        try
        {
            EnsureInitialized(null);
            var emuApplyConfigs = SacConfig.EMUApplyConfigs.GetEMUApplyConfig();
            return new EMUApply().CheckGoldberg(emuApplyConfigs);
        }
        catch
        {
            return false;
        }
    }

    public string? GetEmuVersion()
    {
        try
        {
            EnsureInitialized(null);
            var dir = SacConfig.GoldbergPath;
            var path = Path.Combine(dir, "version");
            if (!File.Exists(path)) path = Path.Combine(dir, "commit_id");
            if (!File.Exists(path)) return null;
            var ver = File.ReadLines(path).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(ver) ? null : ver;
        }
        catch
        {
            return null;
        }
    }

    // emu 下载包（Goldberg.7z）用完即删，不堆在 TEMP 里
    private void DeleteEmuArchive()
    {
        try
        {
            var archive = Path.Combine(SacConfig.TempPath, "Goldberg.7z");
            if (File.Exists(archive)) File.Delete(archive);
        }
        catch (Exception ex)
        {
            LogService.Warn("免启动", $"清理 emu 下载包失败: {ex.Message}");
        }
    }

    private void EnsureInitialized(IProgress<string>? log)
    {
        lock (_initLock)
        {
            // 状态查询（IsEmuInstalled/GetEmuVersion）传 null：不许碰进行中任务的进度
            if (log != null) _activeProgress = log;
            if (_initialized) return;
            var root = CacheRoot;
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "TEMP"));
            Directory.CreateDirectory(Path.Combine(root, "Goldberg"));
            // 必须在首次触碰 Config 前重定向：静态初始值全指向程序目录
            SacConfig.ConfigPath = Path.Combine(root, "config.json");
            SacConfig.TempPath = Path.Combine(root, "TEMP");
            SacConfig.GoldbergPath = Path.Combine(root, "Goldberg");
            SacConfig.EMUConfigPath = Path.Combine(root, "TEMP", "steam_settings");
            // Core 内不写配置文件（保持内存默认），日志只进我们的管道
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Sink(new ForwardingSink(this))
                .CreateLogger();
            _initialized = true;
        }
    }

    // Processor 只打日志不抛错（catch 内吞）：靠 Error/Fatal 判定成败；
    // UI 只收 Information 以上，Debug 只进 app.log 文件，否则刷屏
    private sealed class ForwardingSink(CrackToolService owner) : ILogEventSink
    {
        public void Emit(LogEvent e)
        {
            try
            {
                var text = e.RenderMessage();
                if (e.Exception != null)
                    text += $"：{e.Exception.GetType().Name}: {e.Exception.Message}";
                if (e.Level is LogEventLevel.Error or LogEventLevel.Fatal)
                {
                    owner._runHadError = true;
                    owner._activeProgress?.Report(text);
                    LogService.Error("免启动", text);
                }
                else if (e.Level == LogEventLevel.Warning)
                {
                    owner._activeProgress?.Report(text);
                    LogService.Warn("免启动", text);
                }
                else if (e.Level == LogEventLevel.Information)
                {
                    owner._activeProgress?.Report(text);
                    LogService.Info("免启动", text);
                }
                else
                {
                    LogService.Info("免启动", "[调试] " + text);
                }
            }
            catch { }
        }
    }
}
