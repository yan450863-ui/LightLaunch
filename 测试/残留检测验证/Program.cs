using LightLaunch;
using Microsoft.Win32;

// All filesystem and registry observations are supplied by FakeProbe. No UI,
// process launch, registry access, fixture creation or cleanup is performed.
var passed = 0;
var failed = new List<string>();
const string folder = @"Q:\ResidueFixtures\BowPad";
const string main = @"Q:\ResidueFixtures\BowPad\BowPad.exe";
const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BowPad";
var roots = new ResidueScanRoots(@"Q:\ResidueFixtures\Local", @"Q:\ResidueFixtures\Roaming", @"Q:\ResidueFixtures\ProgramData");
var registration = new UninstallRegistration("LocalMachine|Registry64|BowPad", "BowPad", folder, "unused.exe", "StefansTools");
var historical = ResidueSnapshot.Create("BowPad", folder, main, registration, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));

void Check(string name, bool condition)
{
    if (condition) { passed++; Console.WriteLine("PASS " + name); }
    else { failed.Add(name); Console.WriteLine("FAIL " + name); }
}
FakeProbe Fresh()
{
    var probe = new FakeProbe();
    foreach (var root in new[] { roots.LocalAppData, roots.RoamingAppData, roots.ProgramData })
        probe.Paths[root] = new(ResiduePresence.Present, IsDirectory: true);
    return probe;
}
ResidueScanResult Scan(FakeProbe probe, ResidueSnapshot? snapshot = null, UninstallObservation? observation = null)
    => new ResidueService(probe, roots).Scan(snapshot ?? historical, observation);

{
    var probe = Fresh();
    var result = Scan(probe);
    Check("Historical snapshots retain paths without probing existence", historical.MainExecutablePath == main && historical.OriginalFolderPath == folder);
    Check("Absent original directory and exact registration confirm completion", result.Completion == UninstallCompletionState.Completed);
    Check("All absent candidate locations produce a clean scoped result", result.NoCandidatesInCheckedScope && result.ScanComplete && result.Findings.Count == 0);
    Check("Absent original directory proves internal main absent without accessing it", !probe.PathCalls.Contains(main));
}
{
    var probe = Fresh();
    probe.Registry[(RegistryHive.LocalMachine, RegistryView.Registry64, uninstallKey)] = new(ResiduePresence.Present);
    var result = Scan(probe);
    Check("Historical BowPad key still present prevents completion", result.Completion == UninstallCompletionState.Pending);
    Check("Exact original registration is checked despite absent main directory", probe.RegistryCalls.Contains((RegistryHive.LocalMachine, RegistryView.Registry64, uninstallKey)));
    Check("Existing uninstall key is an unverified finding", result.Findings.Any(x => x.Source == "OriginalRegistration" && x.Confidence == ResidueConfidence.Unverified));
}
{
    var probe = Fresh();
    probe.Paths[folder] = new(ResiduePresence.Present, Identity: "original", IsDirectory: true);
    probe.Paths[main] = new(ResiduePresence.Present, IsDirectory: false);
    var result = Scan(probe, historical with { OriginalDirectoryIdentity = "original" });
    Check("Present main executable prevents completion even when key absent", result.Completion == UninstallCompletionState.Pending);
    Check("Pending original directory is not labeled verified remnant", result.Findings.Single(x => x.Source == "OriginalDirectory").Confidence == ResidueConfidence.Unverified);
}
{
    var probe = Fresh();
    probe.Paths[folder] = new(ResiduePresence.Present, Identity: "original", IsDirectory: true);
    var service = new ResidueService(probe, roots);
    var item = new SoftwareItem { Name = "BowPad", FolderPath = folder, Preference = new() { EntryRelativePath = "BowPad.exe" } };
    var snapshot = service.CaptureSnapshot(item, registration);
    Check("Capture records injected directory identity and entry", snapshot.OriginalDirectoryIdentity == "original" && snapshot.MainExecutablePath == main);
    var same = service.Scan(snapshot);
    Check("Same original directory identity with absent main and key is verified remnant", same.Completion == UninstallCompletionState.Completed && same.Findings.Single(x => x.Source == "OriginalDirectory").Confidence == ResidueConfidence.VerifiedRemnant);
    Check("Verified remnant prevents claim of no candidates", !same.NoCandidatesInCheckedScope);
    probe.Paths[folder] = new(ResiduePresence.Present, Identity: "replacement", IsDirectory: true);
    var replaced = service.Scan(snapshot);
    Check("Reused original path with changed identity is possible shared", replaced.Findings.Single(x => x.Source == "OriginalDirectory").Confidence == ResidueConfidence.PossibleShared);
    probe.Paths[folder] = new(ResiduePresence.Present, IsDirectory: true);
    var unknownIdentity = service.Scan(snapshot);
    Check("Missing current directory identity is possible shared", unknownIdentity.Findings.Single(x => x.Source == "OriginalDirectory").Confidence == ResidueConfidence.PossibleShared);
}
foreach (var (observation, expected) in new[]
{
    (new UninstallObservation(UninstallRunState.Running), UninstallCompletionState.Pending),
    (new UninstallObservation(UninstallRunState.Cancelled), UninstallCompletionState.Cancelled),
    (new UninstallObservation(UninstallRunState.Failed), UninstallCompletionState.Failed),
    (new UninstallObservation(UninstallRunState.Exited, 1), UninstallCompletionState.Failed),
    (new UninstallObservation(UninstallRunState.Exited, 1603), UninstallCompletionState.Failed),
    (new UninstallObservation(UninstallRunState.Exited, 1602), UninstallCompletionState.Cancelled),
    (new UninstallObservation(UninstallRunState.Exited, 0), UninstallCompletionState.Completed)
})
{
    var result = Scan(Fresh(), observation: observation);
    Check($"Observation {observation.State}/{observation.ExitCode} respects process outcome", result.Completion == expected);
    if (expected != UninstallCompletionState.Completed)
        Check($"Observation {observation.State}/{observation.ExitCode} cannot claim scoped cleanliness", !result.NoCandidatesInCheckedScope);
}
foreach (var state in new[] { ResiduePresence.Unknown, ResiduePresence.SkippedLink })
{
    var probe = Fresh();
    probe.Paths[folder] = new(ResiduePresence.Present, IsDirectory: true);
    probe.Paths[main] = new(state, "injected uncertainty");
    var result = Scan(probe);
    Check($"{state} main is not completed", result.Completion == UninstallCompletionState.Unknown && !result.NoCandidatesInCheckedScope);
    Check($"{state} main records incomplete scan and warning", !result.ScanComplete && result.Warnings.Count > 0);

    probe = Fresh();
    probe.Registry[(RegistryHive.LocalMachine, RegistryView.Registry64, uninstallKey)] = new(state);
    result = Scan(probe);
    Check($"{state} exact original key is not completed", result.Completion == UninstallCompletionState.Unknown && !result.NoCandidatesInCheckedScope);

    probe = Fresh();
    probe.Paths[Path.Combine(roots.LocalAppData, "BowPad")] = new(state);
    result = Scan(probe);
    Check($"{state} candidate prevents a clean-scoped claim after completion", result.Completion == UninstallCompletionState.Completed && !result.ScanComplete && !result.NoCandidatesInCheckedScope);

    probe = Fresh();
    probe.Paths[roots.RoamingAppData] = new(state);
    result = Scan(probe);
    Check($"{state} candidate root prevents a clean-scoped claim", !result.ScanComplete && !result.NoCandidatesInCheckedScope);
    Check($"{state} candidate root is not traversed", !probe.PathCalls.Contains(Path.Combine(roots.RoamingAppData, "BowPad")));
}
{
    var probe = Fresh();
    var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var root in new[] { roots.LocalAppData, roots.RoamingAppData, roots.ProgramData })
    {
        foreach (var relative in new[] { "BowPad", @"StefansTools\BowPad" })
        {
            var path = Path.Combine(root, relative);
            expectedPaths.Add(path);
            probe.Paths[path] = new(ResiduePresence.Present, IsDirectory: true);
        }
        probe.Paths[Path.Combine(root, "StefansTools")] = new(ResiduePresence.Present, IsDirectory: true);
        probe.Paths[Path.Combine(root, "BowPadBackup")] = new(ResiduePresence.Present, IsDirectory: true);
    }
    var result = Scan(probe);
    Check("All six exact app-data candidates are detected", expectedPaths.All(path => result.Findings.Any(x => x.Location == path)));
    Check("App-data matches remain possible shared data", result.Findings.Count == 6 && result.Findings.All(x => x.Confidence == ResidueConfidence.PossibleShared));
    Check("Shared publisher parent is never separately probed", !probe.PathCalls.Any(x => x.EndsWith(@"\StefansTools", StringComparison.OrdinalIgnoreCase)));
    Check("Prefix-only software names are never probed", !probe.PathCalls.Any(x => x.EndsWith(@"\BowPadBackup", StringComparison.OrdinalIgnoreCase)));
    Check("No directory enumeration or broad candidate access is possible", probe.PathCalls.All(x => x == folder || x == main || new[] { roots.LocalAppData, roots.RoamingAppData, roots.ProgramData }.Contains(x) || expectedPaths.Contains(x)));
}
{
    var probe = Fresh();
    var expected = new HashSet<(RegistryHive, RegistryView, string)>();
    foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
    foreach (var view in hive == RegistryHive.CurrentUser ? new[] { RegistryView.Registry64 } : new[] { RegistryView.Registry32, RegistryView.Registry64 })
    foreach (var path in new[] { @"Software\BowPad", @"Software\StefansTools\BowPad" })
    {
        expected.Add((hive, view, path));
        probe.Registry[(hive, view, path)] = new(ResiduePresence.Present);
    }
    var result = Scan(probe);
    Check("Shared HKCU and distinct HKLM views probe six precise candidates", expected.All(x => probe.RegistryCalls.Contains(x)));
    Check("Six software-registry candidates remain possible shared", result.Findings.Count == 6 && result.Findings.All(x => x.Source == "SoftwareRegistry" && x.Confidence == ResidueConfidence.PossibleShared));
    Check("Publisher-only or prefix keys are not probed", probe.RegistryCalls.All(x => expected.Contains(x) || x == (RegistryHive.LocalMachine, RegistryView.Registry64, uninstallKey)));
}
{
    var probe = Fresh();
    var noMain = historical with { MainExecutablePath = null };
    Check("No-main historical snapshot can complete if entire original folder absent", Scan(probe, noMain).Completion == UninstallCompletionState.Completed);
    probe.Paths[folder] = new(ResiduePresence.Present, IsDirectory: true);
    Check("No-main snapshot with retained folder remains unknown", Scan(probe, noMain).Completion == UninstallCompletionState.Unknown);
    Check("Invalid original registration ID remains unknown", Scan(Fresh(), historical with { Registration = registration with { Id = "invalid" } }).Completion == UninstallCompletionState.Unknown);
    var cancelled = false;
    try { new ResidueService(Fresh(), roots).Scan(historical, cancellationToken: new CancellationToken(true)); }
    catch (OperationCanceledException) { cancelled = true; }
    Check("Cancelled scan propagates cancellation without partial clean result", cancelled);
}

foreach (var state in new[] { ResiduePresence.Unknown, ResiduePresence.SkippedLink })
{
    var probe = Fresh();
    probe.Paths[folder] = new(state);
    var result = Scan(probe);
    Check($"{state} original directory cannot imply absent internal executable", result.Completion == UninstallCompletionState.Unknown && !result.NoCandidatesInCheckedScope);
    Check($"{state} original directory does not probe its executable child", !probe.PathCalls.Contains(main));
}
{
    // A with-expression simulates untrusted historical JSON bypassing Create().
    var probe = Fresh();
    bool rejected = false;
    try { Scan(probe, historical with { MainExecutablePath = @"Q:\Outside\BowPad.exe" }); }
    catch (ArgumentException) { rejected = true; }
    Check("Historical main path outside original directory is rejected before probes", rejected && probe.PathCalls.Count == 0 && probe.RegistryCalls.Count == 0);

    probe = Fresh();
    rejected = false;
    try { Scan(probe, historical with { OriginalFolderPath = @"Q:\", MainExecutablePath = null }); }
    catch (ArgumentException) { rejected = true; }
    Check("Historical drive-root directory is rejected before probes", rejected && probe.PathCalls.Count == 0 && probe.RegistryCalls.Count == 0);
}
{
    var probe = Fresh();
    var foreignAlias = "UnrelatedProduct";
    probe.Paths[Path.Combine(roots.LocalAppData, foreignAlias)] = new(ResiduePresence.Present, IsDirectory: true);
    var result = Scan(probe, historical with { Names = new[] { "BowPad", foreignAlias } });
    Check("Historical arbitrary aliases cannot create extra scan targets", !probe.PathCalls.Any(x => x.Contains(foreignAlias)) && !probe.RegistryCalls.Any(x => x.Key.Contains(foreignAlias)));
    Check("Ignored historical aliases record warning and prevent clean claim", result.Warnings.Count > 0 && !result.ScanComplete && !result.NoCandidatesInCheckedScope);
}

foreach (var name in new[] { "微信", "QQ", "钉钉" })
{
    var probe = Fresh();
    var result = Scan(probe, ResidueSnapshot.Create(name, folder, main, registration with { DisplayName = name }));
    Check(name + " short name completes", result.ScanComplete && result.NoCandidatesInCheckedScope);
    Check(name + " exact candidate is checked", probe.RegistryCalls.Contains((RegistryHive.CurrentUser, RegistryView.Registry64, "Software\\" + name)));
    Check(name + " shared HKCU is not checked twice", !probe.RegistryCalls.Contains((RegistryHive.CurrentUser, RegistryView.Registry32, "Software\\" + name)));
}

Console.WriteLine($"Residue checks: {passed} passed, {failed.Count} failed. Fake probes only; no system mutations or UI.");
Environment.ExitCode = failed.Count == 0 ? 0 : 1;

sealed class FakeProbe : IResidueProbe
{
    public Dictionary<string, ResidueProbeResult> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(RegistryHive Hive, RegistryView View, string Key), ResidueProbeResult> Registry { get; } = new();
    public HashSet<string> PathCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<(RegistryHive Hive, RegistryView View, string Key)> RegistryCalls { get; } = new();
    public ResidueProbeResult InspectPath(string path)
    {
        PathCalls.Add(path);
        return Paths.TryGetValue(path, out var value) ? value : new(ResiduePresence.Absent);
    }
    public ResidueProbeResult InspectRegistry(RegistryHive hive, RegistryView view, string keyPath)
    {
        RegistryCalls.Add((hive, view, keyPath));
        return Registry.TryGetValue((hive, view, keyPath), out var value) ? value : new(ResiduePresence.Absent);
    }
}
