using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightLaunch;

public sealed record ResidueHistoryEntry(string Key, string Path, ResidueScanResult Report)
{
    public bool NeedsAttention => !Report.NoCandidatesInCheckedScope;
    public string State => Report.Completion != UninstallCompletionState.Completed ? "卸载待确认"
        : Report.Findings.Count > 0 ? $"{Report.Findings.Count} 项候选残留"
        : Report.ScanComplete ? "已无候选残留" : "部分位置待核对";
    public override string ToString() => Report.Snapshot.SoftwareName + " · " + State;
}
public sealed record ResidueHistoryList(IReadOnlyList<ResidueHistoryEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>Current per-installation reports, including compatibility with earlier timestamped JSON files.</summary>
public sealed class ResidueHistoryStore
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly string root;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    public ResidueHistoryStore(string directory) => root = DirectoryJunction.LocalPath(directory);

    public ResidueHistoryList Load()
    {
        lock (gate) {
            var entries = new List<ResidueHistoryEntry>(); var warnings = new List<string>();
            ValidateLocation(root);
            if (!Directory.Exists(root)) return new(entries, warnings);
            foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)) {
                try { var result = Read(path); entries.Add(new(Key(result.Snapshot), path, result)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) {
                    warnings.Add(Path.GetFileName(path) + "：" + ex.Message);
                }
            }
            // A later scan of an old uninstall must never displace a newer installation's snapshot.
            var current = entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.Report.Snapshot.CapturedAt).ThenByDescending(e => e.Report.ScannedAt)
                    .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).First())
                .OrderByDescending(e => e.NeedsAttention).ThenByDescending(e => e.Report.ScannedAt).ToArray();
            return new(current, warnings);
        }
    }

    public string Save(ResidueScanResult result, string? existingPath = null)
    {
        lock (gate) {
            Validate(result); ValidateLocation(root); Directory.CreateDirectory(root); ValidateLocation(root);
            string key = Key(result.Snapshot);
            string path = existingPath is null ? Path.Combine(root, $"{key}-{result.Snapshot.CapturedAt.UtcTicks:X}.json") : OwnedPath(existingPath);
            var latest = Load().Entries.FirstOrDefault(e => e.Key == key);
            if (latest is not null && (latest.Report.Snapshot.CapturedAt > result.Snapshot.CapturedAt
                || latest.Report.Snapshot.CapturedAt == result.Snapshot.CapturedAt && latest.Report.ScannedAt > result.ScannedAt))
                throw new IOException("此软件已有较新的检测记录，请重新选择后检测。");
            if (File.Exists(path)) {
                var previous = Read(path);
                if (Key(previous.Snapshot) != key || previous.Snapshot.CapturedAt != result.Snapshot.CapturedAt)
                    throw new IOException("检测记录对应的软件或卸载批次已变化，未覆盖。");
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("检测记录过大，未保存。");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(bytes); stream.Flush(true);
                }
                ValidateLocation(path); File.Move(temporary, path, true);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
    }

    private ResidueScanResult Read(string path)
    {
        path = OwnedPath(path); ValidateLocation(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("检测记录过大，已跳过。");
        var result = JsonSerializer.Deserialize<ResidueScanResult>(stream, Json) ?? throw new InvalidDataException("检测记录为空。");
        Validate(result); return result;
    }

    private string OwnedPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("检测记录不在指定保存目录内。");
        return path;
    }

    private static string Key(ResidueSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        snapshot.Registration.Id.ToUpperInvariant() + "\n" + DirectoryJunction.LocalPath(snapshot.OriginalFolderPath).ToUpperInvariant())))[..32];

    private static void Validate(ResidueScanResult result)
    {
        if (result.Snapshot is not { Registration: { } registration } snapshot
            || string.IsNullOrWhiteSpace(snapshot.SoftwareName) || string.IsNullOrWhiteSpace(registration.Id)
            || string.IsNullOrWhiteSpace(registration.DisplayName) || snapshot.Names is null || snapshot.Warnings is null
            || result.Findings is null || result.Checks is null || result.Warnings is null || result.Summary is null
            || snapshot.CapturedAt == default || result.ScannedAt == default || !Enum.IsDefined(result.Completion))
            throw new InvalidDataException("检测记录缺少必要信息。");
        _ = DirectoryJunction.LocalPath(snapshot.OriginalFolderPath);
        if (snapshot.MainExecutablePath is string main && (!Path.IsPathFullyQualified(main) || !LibraryService.IsPathInside(snapshot.OriginalFolderPath, main)))
            throw new InvalidDataException("检测记录的主程序位置无效。");
        if (result.Findings.Count > 10000 || result.Checks.Count > 10000
            || result.Findings.Any(f => f is null || string.IsNullOrWhiteSpace(f.Location) || string.IsNullOrWhiteSpace(f.Source) || !Enum.IsDefined(f.Confidence))
            || result.Checks.Any(c => c is null || string.IsNullOrWhiteSpace(c.Location) || string.IsNullOrWhiteSpace(c.Source) || !Enum.IsDefined(c.Presence)))
            throw new InvalidDataException("检测记录的候选或检查信息无效。");
    }

    private static void ValidateLocation(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("检测记录路径包含链接，未读取或写入。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
