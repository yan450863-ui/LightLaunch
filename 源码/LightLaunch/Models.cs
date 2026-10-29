namespace LightLaunch;

public enum AppKind { Unknown, Portable, Installed }
public sealed class AppPreference
{
    public string? EntryRelativePath { get; set; }
    public string Arguments { get; set; } = "";
    public string? WorkingDirectoryRelative { get; set; }
    public AppKind Kind { get; set; }
    public string? UninstallRegistryId { get; set; }
}
public sealed class LauncherSettings
{
    public string? LibraryRoot { get; set; }
    public Dictionary<string, AppPreference> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FolderIdentities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class SoftwareItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public List<string> CandidateEntries { get; set; } = new();
    public AppPreference Preference { get; set; } = new();
    public string? EntryPath => string.IsNullOrWhiteSpace(Preference.EntryRelativePath) ? null : Path.GetFullPath(Path.Combine(FolderPath, Preference.EntryRelativePath));
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
