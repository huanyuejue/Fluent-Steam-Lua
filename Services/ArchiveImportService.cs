using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SteamLuaManager.Services;

public sealed record ArchiveImportResult(
    int LuaCount,
    int BinCount,
    int ManifestCount,
    int SkippedCount,
    List<string> SkippedFiles,
    List<string> FailedFiles);

public interface IArchiveImportService
{
    bool IsArchive(string path);
    Task<ArchiveImportResult> ImportAsync(string archivePath, IProgress<string>? status, CancellationToken ct = default);
}

public class ArchiveImportService : IArchiveImportService
{
    private readonly ILuaFileManager _luaFiles;

    private static readonly string[] ArchiveExts = [".zip", ".tar", ".7z", ".rar"];
    private static readonly string[] ContentExts = [".lua", ".bin", ".manifest"];

    // 炸弹包防护：解压总量与条目数上限
    private const long MaxTotalBytes = 1L * 1024 * 1024 * 1024;
    private const int MaxEntries = 10000;

    public ArchiveImportService(ILuaFileManager luaFiles)
    {
        _luaFiles = luaFiles;
    }

    public bool IsArchive(string path) =>
        ArchiveExts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<ArchiveImportResult> ImportAsync(string archivePath, IProgress<string>? status, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            throw new InvalidOperationException("压缩包不存在");
        if (!IsArchive(archivePath))
            throw new InvalidOperationException("不支持的压缩包格式，仅支持 zip/tar/7z/rar");

        var workDir = Path.Combine(Path.GetTempPath(), "SteamLuaManager", "archive_" + Guid.NewGuid().ToString("N"));
        var extracted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(workDir);
            status?.Report("正在解压...");
            ExtractEntries(archivePath, workDir, seen, skipped, extracted, ct);

            var result = new ArchiveImportResult(0, 0, 0, skipped.Count, skipped, new List<string>());
            foreach (var file in extracted)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        await _luaFiles.AddLuaFileAsync(file);
                        result = result with { LuaCount = result.LuaCount + 1 };
                    }
                    else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        await _luaFiles.AddBinFileAsync(file);
                        result = result with { BinCount = result.BinCount + 1 };
                    }
                    else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        await _luaFiles.AddManifestFileAsync(file);
                        result = result with { ManifestCount = result.ManifestCount + 1 };
                    }
                }
                catch (Exception ex)
                {
                    result.FailedFiles.Add($"{Path.GetFileName(file)}：{ex.Message}");
                }
            }
            LogService.Info("主页", $"压缩包导入：游戏 {result.LuaCount}，成就 {result.BinCount}，清单 {result.ManifestCount}，跳过同名 {result.SkippedCount}，失败 {result.FailedFiles.Count}");
            return result;
        }
        finally
        {
            // 临时解压目录必清，成功失败都一样；父目录空了也一并收掉
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            try
            {
                var parent = Path.GetDirectoryName(workDir);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch { }
        }
    }

    // 按文件名展平提取：只取文件名天然免疫 Zip Slip；同名后者跳过
    private static void ExtractEntries(string archivePath, string workDir, HashSet<string> seen, List<string> skipped, List<string> extracted, CancellationToken ct)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        using var streams = new DisposableList();
        var entries = ListEntries(archivePath, ext, streams);
        long totalBytes = 0;
        int count = 0;
        foreach (var (name, size, open) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(name)) continue;
            if (!ContentExts.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) continue;
            var fileName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(fileName)) continue;
            if (++count > MaxEntries)
                throw new InvalidOperationException($"压缩包条目超过 {MaxEntries} 个，已中止");
            if (size > 0)
            {
                totalBytes += size;
                if (totalBytes > MaxTotalBytes)
                    throw new InvalidOperationException("解压后体积超过 1GB，已中止");
            }
            if (!seen.Add(fileName))
            {
                skipped.Add(fileName);
                continue;
            }
            var dest = Path.Combine(workDir, fileName);
            using var src = open();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
            extracted.Add(dest);
        }
    }

    // 条目枚举与流打开分离：流在写入目标时才打开；
    // SharpCompress 的 Reader 为单遍前向，枚举期间保持打开、按序消费一次
    private sealed record Entry(string Name, long Size, Func<Stream> Open);

    private static IEnumerable<Entry> ListEntries(string archivePath, string ext, DisposableList streams)
    {
        return ext switch
        {
            ".zip" => ListZipEntries(archivePath, streams),
            ".tar" => ListTarEntries(archivePath, streams),
            ".rar" => ListRarEntries(archivePath, streams),
            // 7z 不可流式读取，走 Archive 整包展平后统一扫描
            ".7z" => ExtractSevenZipFlat(archivePath, streams),
            _ => throw new InvalidOperationException("不支持的压缩包格式，仅支持 zip/tar/7z/rar")
        };
    }

    private static List<Entry> ListZipEntries(string path, DisposableList streams)
    {
        var zip = ZipFile.OpenRead(path);
        streams.Add(zip);
        var list = new List<Entry>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var captured = entry;
            list.Add(new Entry(captured.FullName, captured.Length, () => captured.Open()));
        }
        return list;
    }

    private static List<Entry> ListTarEntries(string path, DisposableList streams)
    {
        // tar 为顺序流：先全量读入内存再逐个落盘，包内文件均为小体量清单类文件
        var list = new List<Entry>();
        using var fs = File.OpenRead(path);
        using var reader = new TarReader(fs);
        TarEntry? item;
        while ((item = reader.GetNextEntry()) != null)
        {
            if (item.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile) continue;
            using var ms = new MemoryStream();
            item.DataStream?.CopyTo(ms);
            var bytes = ms.ToArray();
            var name = item.Name;
            list.Add(new Entry(name, bytes.Length, () => new MemoryStream(bytes, writable: false)));
        }
        return list;
    }

    private static IEnumerable<Entry> ExtractSevenZipFlat(string path, DisposableList streams)
    {
        var stageDir = Path.Combine(Path.GetTempPath(), "SteamLuaManager", "stage_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stageDir);
            IArchive archive;
            try
            {
                archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法打开压缩包（可能是加密包或已损坏）：{ex.Message}", ex);
            }
            streams.Add(archive);
            var options = new ExtractionOptions { ExtractFullPath = false };
            foreach (var entry in archive.Entries)
            {
                try
                {
                    entry.WriteToDirectory(stageDir, options);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"无法解压（可能是加密包或已损坏）：{ex.Message}", ex);
                }
            }
        }
        catch
        {
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            throw;
        }
        streams.Add(new DeleteOnDispose(stageDir));
        var list = new List<Entry>();
        foreach (var file in Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories))
        {
            var captured = file;
            list.Add(new Entry(Path.GetFileName(file), new FileInfo(file).Length, () => File.OpenRead(captured)));
        }
        return list;
    }

    // rar 走单遍前向 Reader：枚举期间保持打开，调用方按序消费一次
    private static IEnumerable<Entry> ListRarEntries(string path, DisposableList streams)
    {
        IReader reader;
        try
        {
            reader = ReaderFactory.OpenReader(path, new ReaderOptions());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法打开压缩包（可能是加密包或已损坏）：{ex.Message}", ex);
        }
        streams.Add(reader);
        while (reader.MoveToNextEntry())
        {
            var e = reader.Entry;
            if (e.IsDirectory) continue;
            if (e.IsEncrypted)
                throw new InvalidOperationException("不支持加密压缩包");
            var name = e.Key ?? string.Empty;
            var size = e.Size;
            yield return new Entry(name, size, () => reader.OpenEntryStream());
        }
    }

    // 跨格式持有的可释放资源随单次导入结束统一释放
    private sealed class DisposableList : IDisposable
    {
        private readonly List<IDisposable> _items = new();
        public void Add(IDisposable item) => _items.Add(item);
        public void Dispose()
        {
            foreach (var item in _items)
            {
                try { item.Dispose(); } catch { }
            }
        }
    }

    // 7z 中转目录随导入结束删除
    private sealed class DeleteOnDispose : IDisposable
    {
        private readonly string _dir;
        public DeleteOnDispose(string dir) => _dir = dir;
        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }
}
