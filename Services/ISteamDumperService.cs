using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SteamLuaManager.Services;

public class DumperResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string OutputDirectory { get; set; } = "";
    public List<string> ExtractedFiles { get; set; } = [];
}

public interface ISteamDumperService
{
    event System.Action<string>? LogLineReceived;
    Task<DumperResult> RunAsync(string appId, bool pinManifest, CancellationToken ct = default);
}
