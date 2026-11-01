namespace LightLaunch;

public enum AppKind { Unknown, Portable, Installed }
public enum LibraryLayout { Categorized, Direct }
public enum LibrarySort { Recent, Name }
public sealed class AppPreference
{
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Picture1 { get; set; }
    public string? Picture2 { get; set; }
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public bool SingleFile { get; set; }
    public string? EntryRelativePath { get; set; }
    public string Arguments { get; set; } = "";
    public string? WorkingDirectoryRelative { get; set; }
    public AppKind Kind { get; set; }
    public string? UninstallRegistryId { get; set; }
}
public sealed class LauncherSettings
{
    public LibraryLayout Layout { get; set; }
    public LibrarySort Sort { get; set; } = LibrarySort.Recent;
    public string? LibraryRoot { get; set; }
    public string? LibraryRootIdentity { get; set; }
    public Dictionary<string, AppPreference> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FolderIdentities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PreviousLocations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class SoftwareItem
{
    public LibraryLayout Layout { get; set; }
    public string Id { get; set; } = "";
    public string? SingleFilePath { get; set; }
    public bool IsSingleFile => SingleFilePath is not null;
    public string StoragePath => SingleFilePath ?? FolderPath;
    public string Name { get; set; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Preference.DisplayName) ? Name : Preference.DisplayName;
    public void SetDisplayName(string name)
    {
        name = name.Trim();
        if (name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("显示名称最多 120 个字，不能包含换行或控制字符。");
        Preference.DisplayName = name.Length == 0 || name == Name ? null : name;
    }
    public string Category { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public List<string> CandidateEntries { get; set; } = new();
    public AppPreference Preference { get; set; } = new();
    public string? EntryPath => SingleFilePath ?? (string.IsNullOrWhiteSpace(Preference.EntryRelativePath) ? null : Path.GetFullPath(Path.Combine(FolderPath, Preference.EntryRelativePath)));
    public long? SizeBytes { get; set; }
    public bool SizeIncomplete { get; set; }
    public DateTime? SizeCalculatedAt { get; set; }
    public string? DiscoveryWarning { get; set; }
}
public sealed record LibraryScanResult(bool RootAvailable, List<SoftwareItem> Items, List<string> Warnings);
public sealed record FolderSizeResult(long Bytes, bool Incomplete, DateTime CalculatedAt);
public sealed record UninstallRegistration(string Id, string DisplayName, string? InstallLocation, string Command, string? Publisher, string? DisplayIcon = null);
public sealed class UninstallRegistrationList(IEnumerable<UninstallRegistration> entries, bool readComplete)
    : List<UninstallRegistration>(entries)
{
    public bool ReadComplete { get; } = readComplete;
}
public sealed record UninstallProposal(AppKind Kind, UninstallRegistration? Registration, bool IsAutomatic, string? Reason = null);
public sealed record TrackedProcessState(bool IsRunning, bool IsUncertain, int ProcessCount, string Description);
public sealed record StopResult(bool Completed, bool CanForce, string Message);
