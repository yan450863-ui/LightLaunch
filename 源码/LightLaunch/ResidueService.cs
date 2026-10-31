using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

/// <summary>Bounded, read-only post-uninstall checks. A match is never authorization to delete it.</summary>
public sealed class ResidueService
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IResidueProbe probe;
    private readonly ResidueScanRoots roots;
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Software", "Microsoft", "Windows", "System", "System32", "Program Files", "Program Files (x86)",
        "Common Files", "ProgramData", "AppData", "Local", "Roaming", "Temp", "Apps", "Programs", "Data",
        "Users", "Application Data", "Default", "Public", "Unknown", "Config", "Settings", "Cache", "Logs",
        "Packages", "Package Cache", "User Data", "App", "Application", "Common", "未确认", "未设定"
    };

    public ResidueService(IResidueProbe? probe = null, ResidueScanRoots? roots = null)
    {
        this.probe = probe ?? new WindowsResidueProbe();
        this.roots = roots ?? ResidueScanRoots.CurrentUser();
    }

    public ResidueSnapshot CaptureSnapshot(SoftwareItem item, UninstallRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSingleFile) throw new ArgumentException("单 EXE 不按整个目录检测卸载残留。", nameof(item));
        var snapshot = ValidateSnapshot(ResidueSnapshot.Create(item.Name, item.FolderPath, item.EntryPath, registration));
        var folder = InspectPath(snapshot.OriginalFolderPath);
        return snapshot with
        {
            OriginalDirectoryIdentity = folder.Identity,
            Warnings = folder.Presence is ResiduePresence.Unknown or ResiduePresence.SkippedLink
                ? new[] { "卸载前无法核实原目录身份：" + (folder.Detail ?? folder.Presence.ToString()) } : Array.Empty<string>()
        };
    }

    public ResidueScanResult Scan(ResidueSnapshot snapshot, UninstallObservation? observation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Registration);
        snapshot = ValidateSnapshot(snapshot);
        var findings = new List<ResidueFinding>();
        var checks = new List<ResidueCheck>();
        var warnings = new List<string>(snapshot.Warnings ?? Array.Empty<string>());
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        observation ??= new();
        void Record(string source, string location, ResidueProbeResult result)
        {
            checks.Add(new(source, location, result.Presence, result.Detail ?? result.Presence.ToString()));
            if (result.Presence is ResiduePresence.Unknown or ResiduePresence.SkippedLink)
                warnings.Add(source + "：" + location + " · " + (result.Detail ?? "状态无法核实，未视为不存在。"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var folder = IsBroadOriginalFolder(snapshot.OriginalFolderPath)
            ? new ResidueProbeResult(ResiduePresence.Unknown, "原目录是系统、共享根或无效路径，已拒绝扫描。")
            : InspectPath(snapshot.OriginalFolderPath);
        if (folder.Presence == ResiduePresence.Present && folder.IsDirectory == false)
            folder = new(ResiduePresence.Unknown, "原软件目录位置已变为文件，无法核实原目录。 ");
        Record("OriginalDirectory", snapshot.OriginalFolderPath, folder);

        ResidueProbeResult main;
        if (folder.Presence == ResiduePresence.Absent)
            main = new(ResiduePresence.Absent, "原软件目录已明确不存在，原目录内的入口也不存在。");
        else if (folder.Presence is ResiduePresence.Unknown or ResiduePresence.SkippedLink)
            main = new(folder.Presence, "原软件目录不可读取或经过链接，未将主程序判为不存在。");
        else if (string.IsNullOrWhiteSpace(snapshot.MainExecutablePath))
            main = new(ResiduePresence.Unknown, "未记录原主程序路径，且原目录仍在或不可读取，无法确认主程序已经移除。");
        else if (!LibraryService.IsPathInside(snapshot.OriginalFolderPath, snapshot.MainExecutablePath))
            main = new(ResiduePresence.Unknown, "历史主程序路径超出原软件目录，未访问该路径。");
        else main = InspectPath(snapshot.MainExecutablePath);
        Record("OriginalMainExecutable", snapshot.MainExecutablePath ?? snapshot.OriginalFolderPath + " (目录内入口)", main);

        var registration = InspectOriginalRegistration(snapshot.Registration, out string registrationPath);
        Record("OriginalRegistration", registrationPath, registration);
        var completion = Completion(observation, registration, main);
        if (folder.Presence == ResiduePresence.Present)
        {
            bool sameIdentity = snapshot.OriginalDirectoryIdentity is not null && folder.Identity is not null
                && snapshot.OriginalDirectoryIdentity.Equals(folder.Identity, StringComparison.Ordinal);
            var confidence = completion == UninstallCompletionState.Completed && sameIdentity ? ResidueConfidence.VerifiedRemnant
                : completion == UninstallCompletionState.Completed ? ResidueConfidence.PossibleShared : ResidueConfidence.Unverified;
            findings.Add(new("OriginalDirectory", snapshot.OriginalFolderPath, confidence,
                completion != UninstallCompletionState.Completed ? "原软件目录仍存在，卸载尚未确认完成；不能当作可直接清理项。"
                : sameIdentity ? "原目录身份与卸载前一致，主程序和原卸载登记已移除；目录可能保留配置或个人数据。"
                : "原路径仍存在，但没有相同目录身份的证据；可能已被替换或由其他软件使用。"));
        }
        if (registration.Presence == ResiduePresence.Present)
            findings.Add(new("OriginalRegistration", registrationPath, ResidueConfidence.Unverified,
                "原精确卸载登记仍存在，卸载尚未确认完成；不能据此自动删除登记。"));

        var names = (snapshot.Names ?? Array.Empty<string>()).Append(snapshot.SoftwareName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(name =>
            {
                if (SafeSegment(name, false)) return true;
                warnings.Add("名称不适合精确候选扫描，已跳过：" + name);
                return false;
            }).ToArray();
        string? publisher = snapshot.Registration.Publisher;
        if (!string.IsNullOrWhiteSpace(publisher) && !SafeSegment(publisher, true))
        {
            warnings.Add("发布者不是安全的单级名称，已跳过其组合路径：" + publisher);
            publisher = null;
        }
        var relativeCandidates = names.Select(name => (Path: name, Reason: "软件名称精确匹配"))
            .Concat(string.IsNullOrWhiteSpace(publisher) ? Array.Empty<(string Path, string Reason)>()
                : names.Select(name => (Path: Path.Combine(publisher, name), Reason: "发布者与软件名称的两级路径精确匹配")))
            .DistinctBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in new[] { (Name: "AppData Local", Path: roots.LocalAppData),
                     (Name: "AppData Roaming", Path: roots.RoamingAppData), (Name: "ProgramData", Path: roots.ProgramData) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootState = InspectPath(root.Path);
            if (rootState.Presence == ResiduePresence.Present && rootState.IsDirectory == false)
                rootState = new(ResiduePresence.Unknown, "候选根路径不是目录。");
            Record(root.Name + "Root", root.Path, rootState);
            if (rootState.Presence != ResiduePresence.Present) continue;
            foreach (var candidate in relativeCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(root.Path, candidate.Path));
                if (!LibraryService.IsPathInside(root.Path, path) || !visited.Add("file:" + path)) continue;
                var state = InspectPath(path);
                Record(root.Name, path, state);
                if (state.Presence == ResiduePresence.Present)
                    findings.Add(new(root.Name, path, ResidueConfidence.PossibleShared,
                        candidate.Reason + "；可能是用户数据、共享配置或同名软件，不能据名称判断可删除。"));
            }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var candidate in relativeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string keyPath = "Software\\" + candidate.Path;
            string display = RegistryLocation(hive, view, keyPath);
            if (!visited.Add("registry:" + display)) continue;
            var state = InspectRegistry(hive, view, keyPath);
            Record("SoftwareRegistry", display, state);
            if (state.Presence == ResiduePresence.Present)
                findings.Add(new("SoftwareRegistry", display, ResidueConfidence.PossibleShared,
                    candidate.Reason + "；只检查该精确键，可能属于共享配置或同名产品，不代表可删除。"));
        }
        bool complete = warnings.Count == 0;
        string summary = completion switch
        {
            UninstallCompletionState.Completed => !complete ? "主程序和原登记已移除；残留检测有不可读取或跳过项，不能据此确认无残留。"
                : findings.Count == 0 ? "卸载状态已核实；当前限定范围未发现候选残留。"
                : $"卸载状态已核实；发现 {findings.Count} 项候选残留，归属和是否共享仍需核对。",
            UninstallCompletionState.Pending => "卸载尚未确认完成：卸载程序仍在运行、原登记仍存在或主程序仍存在。",
            UninstallCompletionState.Cancelled => "卸载已取消；以下仅为当前状态，不能当作可直接清理的残留。",
            UninstallCompletionState.Failed => "卸载过程报告失败；以下仅为当前状态，不能当作可直接清理的残留。",
            _ => "无法核实卸载是否完成；存在未知或不可读取的状态。"
        };
        return new(snapshot, DateTimeOffset.Now, completion, complete, summary, findings, checks,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static UninstallCompletionState Completion(UninstallObservation observation, ResidueProbeResult registration, ResidueProbeResult main)
    {
        if (observation.State == UninstallRunState.Running) return UninstallCompletionState.Pending;
        if (observation.State == UninstallRunState.Cancelled || observation.ExitCode == 1602) return UninstallCompletionState.Cancelled;
        if (observation.State == UninstallRunState.Failed || (observation.State == UninstallRunState.Exited
            && observation.ExitCode is not (null or 0 or 3010 or 1641))) return UninstallCompletionState.Failed;
        if (registration.Presence == ResiduePresence.Present || main.Presence == ResiduePresence.Present) return UninstallCompletionState.Pending;
        return registration.Presence == ResiduePresence.Absent && main.Presence == ResiduePresence.Absent
            ? UninstallCompletionState.Completed : UninstallCompletionState.Unknown;
    }

    private ResidueProbeResult InspectOriginalRegistration(UninstallRegistration registration, out string location)
    {
        string[] id = registration.Id.Split('|', 3);
        if (id.Length != 3 || !Enum.TryParse(id[0], out RegistryHive hive) || !Enum.TryParse(id[1], out RegistryView view)
            || hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) || view is not (RegistryView.Registry32 or RegistryView.Registry64)
            || string.IsNullOrWhiteSpace(id[2]) || id[2].IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
        {
            location = registration.Id;
            return new(ResiduePresence.Unknown, "原卸载登记ID不明确，未查询替代或同名登记。");
        }
        string key = UninstallRoot + "\\" + id[2];
        location = RegistryLocation(hive, view, key);
        return InspectRegistry(hive, view, key);
    }

    private ResidueProbeResult InspectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return new(ResiduePresence.Unknown, "绝对路径不可用。");
        try { return probe.InspectPath(Path.GetFullPath(path)); }
        catch (Exception ex) when (IsReadFailure(ex)) { return new(ResiduePresence.Unknown, ex.Message); }
    }
    private ResidueProbeResult InspectRegistry(RegistryHive hive, RegistryView view, string path)
    {
        try { return probe.InspectRegistry(hive, view, path); }
        catch (Exception ex) when (IsReadFailure(ex)) { return new(ResiduePresence.Unknown, ex.Message); }
    }
    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException
        or ArgumentException or NotSupportedException or Win32Exception;
    private static string RegistryLocation(RegistryHive hive, RegistryView view, string path)
        => (hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + (view == RegistryView.Registry64 ? " [64]\\" : " [32]\\") + path;
    private static bool SafeSegment(string? value, bool publisher)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 3 and <= 200 && value == value.Trim() && value is not ("." or "..")
            && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.Contains('%')
            && (publisher || !GenericNames.Contains(value));
    private bool IsBroadOriginalFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return true;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (GenericNames.Contains(Path.GetFileName(folder))) return true;
            var blocked = new[] { Path.GetPathRoot(folder), roots.LocalAppData, roots.RoamingAppData, roots.ProgramData,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            return blocked.Any(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                && folder.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return true; }
    }

    private ResidueSnapshot ValidateSnapshot(ResidueSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SoftwareName) || snapshot.SoftwareName.Length > 256)
            throw new ArgumentException("卸载快照的软件名称无效。");
        if (snapshot.Registration is null || string.IsNullOrWhiteSpace(snapshot.Registration.Id)
            || snapshot.Registration.Id.Length > 1024) throw new ArgumentException("卸载快照缺少明确的原登记ID。");
        if (snapshot.Names is { Count: > 8 } || snapshot.Warnings is { Count: > 32 })
            throw new ArgumentException("卸载快照包含过多别名或诊断，不属于限定范围的检测记录。");
        var normalized = ResidueSnapshot.Create(snapshot.SoftwareName, snapshot.OriginalFolderPath,
            snapshot.MainExecutablePath, snapshot.Registration, snapshot.CapturedAt);
        if (IsBroadOriginalFolder(normalized.OriginalFolderPath))
            throw new ArgumentException("原软件目录是磁盘根、系统或共享泛目录，已拒绝检测。");
        var warnings = new List<string>(snapshot.Warnings ?? Array.Empty<string>());
        // Historical JSON is data, not authority to add arbitrary scan targets. Rebuild aliases from its two original labels.
        if ((snapshot.Names ?? Array.Empty<string>()).Any(name => !normalized.Names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            warnings.Add("快照包含与软件名或登记名不一致的别名，已忽略这些额外扫描目标。");
        return normalized with
        {
            OriginalDirectoryIdentity = snapshot.OriginalDirectoryIdentity,
            Warnings = warnings.Take(32).ToArray()
        };
    }
}

/// <summary>No directory enumeration, registry enumeration, writes, or link traversal.</summary>
public sealed class WindowsResidueProbe : IResidueProbe
{
    public ResidueProbeResult InspectPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return new(ResiduePresence.Unknown, "不是绝对路径。");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full)!;
            var parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return new(ResiduePresence.Unknown, "磁盘或共享根不可访问，不能据此认定路径不存在。"); }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new(ResiduePresence.SkippedLink, "根路径是重解析点，未跟随。");
            foreach (string part in parts)
            {
                if (part == ".") continue;
                if ((attributes & FileAttributes.Directory) == 0) return new(ResiduePresence.Unknown, "上级路径不是目录。");
                current = Path.Combine(current, part);
                attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return new(ResiduePresence.SkippedLink, "路径或祖先包含重解析点，未跟随。");
            }
            bool directory = (attributes & FileAttributes.Directory) != 0;
            return new(ResiduePresence.Present, null, directory ? DirectoryIdentity(full) : null, directory);
        }
        catch (FileNotFoundException) { return new(ResiduePresence.Absent, "精确路径不存在。"); }
        catch (DirectoryNotFoundException) { return new(ResiduePresence.Absent, "精确路径或已核实的上级目录不存在。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        { return new(ResiduePresence.Unknown, ex.Message); }
    }

    public ResidueProbeResult InspectRegistry(RegistryHive hive, RegistryView view, string keyPath)
    {
        if (hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) || view is not (RegistryView.Registry32 or RegistryView.Registry64)
            || !keyPath.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) || keyPath.Contains('\0'))
            return new(ResiduePresence.Unknown, "登记范围无效。");
        int access = 0x20019 | (view == RegistryView.Registry64 ? 0x0100 : 0x0200);
        string current = "";
        foreach (string part in keyPath.Split('\\'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or "..") return new(ResiduePresence.Unknown, "登记路径层级无效。");
            current = current.Length == 0 ? part : current + "\\" + part;
            int result = RegOpenKeyEx(new IntPtr((int)hive), current, 0x00000008, access, out var handle);
            using (handle)
            {
                if (result is 2 or 3) return new(ResiduePresence.Absent, "精确登记键不存在。");
                if (result != 0 || handle.IsInvalid) return new(ResiduePresence.Unknown, "登记读取失败：" + new Win32Exception(result).Message);
                uint length = 0;
                int queried = RegQueryValueEx(handle, "SymbolicLinkValue", IntPtr.Zero, out uint type, null, ref length);
                if ((queried is 0 or 234) && type == 6) return new(ResiduePresence.SkippedLink, "登记路径或祖先是注册表链接，未跟随。");
                if (queried is not (0 or 2 or 234)) return new(ResiduePresence.Unknown, "无法核实登记链接状态：" + new Win32Exception(queried).Message);
            }
        }
        return new(ResiduePresence.Present, "精确键仍存在；不依赖DisplayName或UninstallString字段是否尚在。");
    }

    private static string? DirectoryIdentity(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info)
            ? $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}" : null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(IntPtr key, string path, uint options, int access, out SafeRegistryHandle result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint length);
}
