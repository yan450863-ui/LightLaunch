using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace LightLaunch;

/// <summary>Keeps configuration outside managed software directories.</summary>
public sealed class SettingsStore
{
    private readonly object _saveLock = new();
    private readonly string _dataDirectory;
    private string _writePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LauncherSettings Settings { get; }
    public string? LoadWarning { get; private set; }

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _writePath = Path.Combine(_dataDirectory, "settings.json");
        var primaryPath = _writePath;
        LauncherSettings? loaded = null;

        try
        {
            loaded = ReadSettings(primaryPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            // Never overwrite a damaged/unreadable original. Recovery is a separate file.
            _writePath = Path.Combine(_dataDirectory, "settings.recovered.json");
            string backupMessage;
            try
            {
                string fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(primaryPath)))[..16];
                var backupPath = Path.Combine(_dataDirectory, $"settings.corrupt.{fingerprint}.json");
                if (!File.Exists(backupPath)) File.Copy(primaryPath, backupPath, false);
                backupMessage = $"已另存备份：{Path.GetFileName(backupPath)}。";
            }
            catch (Exception backupError) when (IsLoadFailure(backupError))
            {
                backupMessage = $"原件保留，但无法创建备份：{backupError.Message}。";
            }

            LoadWarning = $"无法读取原配置（{ex.Message}）。{backupMessage}后续配置保存到 settings.recovered.json。";
            try
            {
                // Prefer the most recently written valid recovery if an earlier recovery was also damaged.
                foreach (string recoveryPath in Directory.EnumerateFiles(_dataDirectory, "settings.recovered*.json")
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    try
                    {
                        loaded = ReadSettings(recoveryPath);
                        _writePath = recoveryPath;
                        break;
                    }
                    catch (Exception recoveryError) when (IsLoadFailure(recoveryError))
                    {
                        // Keep every damaged recovery untouched as evidence.
                    }
                }
                if (loaded is null && File.Exists(_writePath))
                    _writePath = Path.Combine(_dataDirectory, $"settings.recovered.{Guid.NewGuid():N}.json");
            }
            catch (Exception recoveryError) when (IsLoadFailure(recoveryError))
            {
                // Preserve this recovery file as well, then use a new recovery file.
                _writePath = Path.Combine(_dataDirectory, $"settings.recovered.{DateTime.Now:yyyyMMdd-HHmmss-fff}.{Guid.NewGuid():N}.json");
                LoadWarning += $"恢复配置也无法读取（{recoveryError.Message}），将保存到 {Path.GetFileName(_writePath)}。";
            }
        }

        Settings = loaded ?? new LauncherSettings { Layout = LibraryLayout.Direct };
        Normalize(Settings);
    }

    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(_dataDirectory);
            string temporaryPath = Path.Combine(_dataDirectory, $".settings.{Guid.NewGuid():N}.tmp");
            try
            {
                byte[] json;
                lock (Settings)
                {
                    Normalize(Settings);
                    json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Settings, JsonOptions));
                }
                if (File.Exists(_writePath) && File.ReadAllBytes(_writePath).AsSpan().SequenceEqual(json)) return;
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(json);
                    stream.Flush(true);
                }

                if (File.Exists(_writePath))
                    File.Replace(temporaryPath, _writePath, null);
                else
                    File.Move(temporaryPath, _writePath);
            }
            finally
            {
                // A failed save keeps the previous complete configuration untouched.
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static LauncherSettings ReadSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<LauncherSettings>(stream, JsonOptions)
            ?? throw new JsonException("配置内容为空。");
    }

    private static void Normalize(LauncherSettings settings)
    {
        if (!Enum.IsDefined(settings.Sort)) settings.Sort = LibrarySort.Recent;
        var apps = new Dictionary<string, AppPreference>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Apps ?? new()) apps[pair.Key] = pair.Value;
        settings.Apps = apps;
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.FolderIdentities ?? new())
            if (!string.IsNullOrWhiteSpace(pair.Value)) identities[pair.Key] = pair.Value;
        settings.FolderIdentities = identities;
        settings.PreviousLocations = new Dictionary<string, string>(settings.PreviousLocations ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in settings.Apps.Keys.ToArray())
        {
            settings.Apps[key] ??= new AppPreference();
            settings.Apps[key].Arguments ??= "";
        }
    }

    private static bool IsLoadFailure(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
