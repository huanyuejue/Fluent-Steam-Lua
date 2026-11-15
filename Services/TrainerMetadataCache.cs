using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public static class TrainerMetadataCache
{
    private static readonly ConcurrentDictionary<string, TrainerMetadata> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, PendingLoad> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ScanGate = new(2, 2);
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class PendingLoad
    {
        public object Sync { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Lazy<Task<TrainerMetadata?>> Work { get; set; } = null!;
        public int Users { get; set; }
        public bool Abandoned { get; set; }
    }

    public static string DirectoryPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "trainers", "options");

    public static async Task<TrainerMetadata?> LoadAsync(string trainerFilePath, CancellationToken ct = default, string? cacheDirectory = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(trainerFilePath)) return null;
        cacheDirectory ??= DirectoryPath;
        var key = Path.GetFullPath(cacheDirectory) + "|" + Path.GetFullPath(trainerFilePath);
        var cached = TryLoadCached(trainerFilePath, cacheDirectory);
        if (cached is { FormatVersion: 2 }) return cached;
        PendingLoad pending;
        Task<TrainerMetadata?> work;
        while (true)
        {
            pending = Pending.GetOrAdd(key, _ =>
            {
                var entry = new PendingLoad();
                entry.Work = new Lazy<Task<TrainerMetadata?>>(() => RunScanAsync(key, entry, trainerFilePath, cacheDirectory));
                return entry;
            });
            lock (pending.Sync)
            {
                if (pending.Abandoned)
                {
                    ((ICollection<KeyValuePair<string, PendingLoad>>)Pending).Remove(new(key, pending));
                    continue;
                }
                pending.Users++;
                work = pending.Work.Value;
                break;
            }
        }
        try { return await work.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (pending.Sync)
            {
                pending.Users--;
                if (pending.Users == 0 && !work.IsCompleted)
                {
                    pending.Abandoned = true;
                    try { pending.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    private static async Task<TrainerMetadata?> RunScanAsync(string key, PendingLoad entry, string source, string directory)
    {
        var acquired = false;
        try
        {
            await ScanGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            return await Task.Run(() => Load(source, directory, entry.Cancellation.Token), entry.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) ScanGate.Release();
            ((ICollection<KeyValuePair<string, PendingLoad>>)Pending).Remove(new(key, entry));
            entry.Cancellation.Dispose();
        }
    }

    public static TrainerMetadata? Load(string trainerFilePath, string? cacheDirectory = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(trainerFilePath)) return null;
        cacheDirectory ??= DirectoryPath;
        var memoryKey = Path.GetFullPath(cacheDirectory) + "|" + Path.GetFullPath(trainerFilePath);
        try
        {
            var source = new FileInfo(trainerFilePath);
            var legacy = TryLoadCached(trainerFilePath, cacheDirectory);
            if (legacy is { FormatVersion: 2 } && MatchesSource(legacy, source)) return legacy;
            if (!source.Exists) return legacy;

            // Scanning runs outside the cache lock, so a UI cache read cannot
            // wait behind a worker thread parsing a large executable.
            var parsed = TrainerExeParser.ReadMetadata(trainerFilePath, ct);
            if (legacy != null) MergeLegacy(parsed, legacy);
            ct.ThrowIfCancellationRequested();
            try { Save(trainerFilePath, parsed, cacheDirectory); }
            catch (Exception ex)
            {
                // A read-only cache must not discard successfully recovered labels.
                Memory[memoryKey] = parsed;
                LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogOptionsCacheFailed", ex.Message));
            }
            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogOptionsCacheFailed", ex.Message));
            return TryLoadCached(trainerFilePath, cacheDirectory);
        }
    }

    // UI bindings only read small cached JSON; executable scans are warmed on a
    // worker thread by the view models or the trainer selection dialog.
    public static TrainerMetadata? TryLoadCached(string trainerFilePath, string? cacheDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(trainerFilePath)) return null;
        cacheDirectory ??= DirectoryPath;
        var memoryKey = Path.GetFullPath(cacheDirectory) + "|" + Path.GetFullPath(trainerFilePath);
        lock (Sync)
        {
            try
            {
                var source = new FileInfo(trainerFilePath);
                if (Memory.TryGetValue(memoryKey, out var memory) && MatchesSource(memory, source)) return memory;
                var path = CachePath(trainerFilePath, cacheDirectory, ".metadata.json");
                if (File.Exists(path))
                {
                    var cached = JsonSerializer.Deserialize<TrainerMetadata>(File.ReadAllText(path));
                    if (cached is { FormatVersion: 2 } && MatchesSource(cached, source))
                    {
                        Memory[memoryKey] = cached;
                        return cached;
                    }
                }
                return LoadLegacy(trainerFilePath, cacheDirectory);
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogOptionsCacheFailed", ex.Message));
                return null;
            }
        }
    }

    public static void Save(string trainerFilePath, TrainerMetadata metadata, string? cacheDirectory = null)
    {
        cacheDirectory ??= DirectoryPath;
        lock (Sync)
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(CachePath(trainerFilePath, cacheDirectory, ".metadata.json"), JsonSerializer.Serialize(metadata, JsonOptions));
            // Keep the old cache shape readable by existing callers, including
            // every hotkey recovered from a legacy cache.
            File.WriteAllText(CachePath(trainerFilePath, cacheDirectory, ".json"), JsonSerializer.Serialize(metadata.Options, JsonOptions));
            var memoryKey = Path.GetFullPath(cacheDirectory) + "|" + Path.GetFullPath(trainerFilePath);
            Memory[memoryKey] = metadata;
        }
    }

    public static void Invalidate(string trainerFilePath)
    {
        var memoryKey = Path.GetFullPath(DirectoryPath) + "|" + Path.GetFullPath(trainerFilePath);
        Memory.TryRemove(memoryKey, out _);
    }

    private static bool MatchesSource(TrainerMetadata metadata, FileInfo source) =>
        !source.Exists || metadata.SourceLength == source.Length && metadata.SourceWriteTimeUtcTicks == source.LastWriteTimeUtc.Ticks;

    private static TrainerMetadata? LoadLegacy(string path, string directory)
    {
        var optionPath = CachePath(path, directory, ".json");
        var namePath = CachePath(path, directory, ".name.txt");
        if (!File.Exists(optionPath) && !File.Exists(namePath)) return null;
        var metadata = new TrainerMetadata { FormatVersion = 1 };
        if (File.Exists(optionPath))
            metadata.Options = JsonSerializer.Deserialize<List<CheatOption>>(File.ReadAllText(optionPath)) ?? new();
        if (File.Exists(namePath)) metadata.OriginalGameName = File.ReadAllText(namePath).Trim();
        if (TrainerExeParser.HasChinese(metadata.OriginalGameName)) metadata.ChineseGameName = metadata.OriginalGameName;
        else metadata.EnglishGameName = metadata.OriginalGameName;
        foreach (var option in metadata.Options)
        {
            if (TrainerExeParser.HasChinese(option.Description))
            {
                if (string.IsNullOrEmpty(option.ChineseDescription)) option.ChineseDescription = option.Description;
            }
            else if (string.IsNullOrEmpty(option.EnglishDescription)) option.EnglishDescription = option.Description;
        }
        return metadata;
    }

    private static void MergeLegacy(TrainerMetadata parsed, TrainerMetadata legacy)
    {
        if (!string.IsNullOrWhiteSpace(legacy.OriginalGameName)) parsed.OriginalGameName = legacy.OriginalGameName;
        if (string.IsNullOrWhiteSpace(parsed.ChineseGameName)) parsed.ChineseGameName = legacy.ChineseGameName;
        if (string.IsNullOrWhiteSpace(parsed.EnglishGameName)) parsed.EnglishGameName = legacy.EnglishGameName;
        foreach (var old in legacy.Options)
        {
            var current = parsed.Options.FirstOrDefault(o => o.FullKey.Equals(old.FullKey, StringComparison.OrdinalIgnoreCase));
            if (current == null) { parsed.Options.Add(old); continue; }
            if (!string.IsNullOrWhiteSpace(old.Description)) current.Description = old.Description;
            if (string.IsNullOrEmpty(current.EnglishDescription)) current.EnglishDescription = old.EnglishDescription;
            if (string.IsNullOrEmpty(current.ChineseDescription)) current.ChineseDescription = old.ChineseDescription;
        }
    }

    private static string CachePath(string source, string directory, string extension)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var key = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return Path.Combine(directory, key + extension);
    }
}
