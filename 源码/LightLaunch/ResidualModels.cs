using Microsoft.Win32;

namespace LightLaunch;

public enum ResiduePresence { Present, Absent, Unknown, SkippedLink }
public enum ResidueConfidence { VerifiedRemnant, PossibleShared, Unverified }
public enum UninstallCompletionState { Completed, Pending, Cancelled, Failed, Unknown }
public enum UninstallRunState { NotObserved, Running, Exited, Cancelled, Failed }

public sealed record UninstallObservation(UninstallRunState State = UninstallRunState.NotObserved, int? ExitCode = null);
public sealed record ResidueProbeResult(ResiduePresence Presence, string? Detail = null, string? Identity = null, bool? IsDirectory = null);
public interface IResidueProbe
{
    ResidueProbeResult InspectPath(string path);
    ResidueProbeResult InspectRegistry(RegistryHive hive, RegistryView view, string keyPath);
}
public sealed record ResidueScanRoots(string LocalAppData, string RoamingAppData, string ProgramData)
{
    public static ResidueScanRoots CurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
}

public sealed record ResidueSnapshot(
    string SoftwareName,
    string OriginalFolderPath,
    string? MainExecutablePath,
    UninstallRegistration Registration,
    DateTimeOffset CapturedAt,
    string? OriginalDirectoryIdentity,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Reconstructs a snapshot from known historical paths without requiring the software still to exist.</summary>
    public static ResidueSnapshot Create(string name, string folder, string? mainExePath,
        UninstallRegistration registration, DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        string? root = Path.GetPathRoot(folder);
        if (!Path.IsPathFullyQualified(folder) || root is null || root.Length != 3 || root[1] != ':' || !char.IsLetter(root[0]))
            throw new ArgumentException("原软件目录必须是本地磁盘上的绝对路径。", nameof(folder));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!string.IsNullOrWhiteSpace(mainExePath))
        {
            if (!Path.IsPathFullyQualified(mainExePath) || !LibraryService.IsPathInside(folder, mainExePath))
                throw new ArgumentException("原主程序路径必须位于原软件目录内。", nameof(mainExePath));
            mainExePath = Path.GetFullPath(mainExePath);
        }
        else mainExePath = null;
        return new(name, folder, mainExePath, registration, capturedAt ?? DateTimeOffset.Now, null,
            new[] { name, registration.DisplayName }.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Array.Empty<string>());
    }
}

public sealed record ResidueFinding(string Source, string Location, ResidueConfidence Confidence, string Reason);
public sealed record ResidueCheck(string Source, string Location, ResiduePresence Presence, string Reason);
public sealed record ResidueScanResult(
    ResidueSnapshot Snapshot,
    DateTimeOffset ScannedAt,
    UninstallCompletionState Completion,
    bool ScanComplete,
    string Summary,
    IReadOnlyList<ResidueFinding> Findings,
    IReadOnlyList<ResidueCheck> Checks,
    IReadOnlyList<string> Warnings)
{
    public bool NoCandidatesInCheckedScope => Completion == UninstallCompletionState.Completed && ScanComplete && Findings.Count == 0;
}
