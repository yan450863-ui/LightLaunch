using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

/// <summary>Discovers software without launching or modifying anything in its folders.</summary>
public sealed class LibraryService
{
    public static string? LibraryRootProblem(string root)
    {
        if (!TryNormalize(root, out var path)) return "请选择有效的软件专用目录。";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && (Paths.Equals(path, windows) || IsPathInside(windows, path)))
            return "不能将 Windows 系统目录作为软件库。";
        var containers = new[] { Path.GetPathRoot(path), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetEnvironmentVariable("PUBLIC") };
        return containers.Where(value => !string.IsNullOrWhiteSpace(value)).Any(value => Paths.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(value!))))
            ? "请选择单独存放软件的子文件夹，不能使用磁盘根目录或系统、用户公共目录。" : null;
    }

    public static bool ShouldRescanChange(string root, LibraryLayout layout, string path, WatcherChangeTypes change)
    {
        if (!IsPathInside(root, path)) return false;
        string relative = Path.GetRelativePath(root, path);
        int depth = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
        bool structure = change != WatcherChangeTypes.Changed && depth <= (layout == LibraryLayout.Direct ? 1 : 2);
        return structure || IsSupportedEntry(path);
    }

    private readonly LauncherSettings _settings;
    private readonly object _scanLock = new();
    private List<SoftwareItem> _lastItems = new();
    private string? _lastRoot;
    private LibraryLayout _lastLayout;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public LibraryService(LauncherSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public LibraryScanResult Scan(string root, CancellationToken cancellationToken)
    {
        lock (_scanLock)
            return ScanCore(root, cancellationToken);
    }

    private LibraryScanResult ScanCore(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var warnings = new WarningList();
        if (LibraryRootProblem(root) is not null || !TryNormalize(root, out var normalizedRoot) || !HasNoReparseAncestors(normalizedRoot, true))
        {
            warnings.Add("软件根目录不可访问，或路径经过了目录链接。原有配置已保留。");
            return new(false, GetKnownItems(root, "根目录当前不可访问，无法核实文件状态。"), warnings.Items);
        }

        var categories = ReadEntries(normalizedRoot, warnings, token, out bool rootReadComplete);
        if (!rootReadComplete)
            return new(false, GetKnownItems(normalizedRoot, "根目录无法完整读取，原有记录已保留。"), warnings.Items);

        var items = new List<SoftwareItem>();
        void AddSingleFile(string path, string categoryName)
        {
            if (!IsSafeStandaloneFile(normalizedRoot, path, _settings.Layout)) return;
            string id = GetOrRememberIdentity(path);
            var preference = GetPreference(id); preference.SingleFile = true;
            preference.EntryRelativePath = Path.GetFileName(path);
            items.Add(new SoftwareItem { Id = id, Layout = _settings.Layout, SingleFilePath = path,
                FolderPath = Path.GetDirectoryName(path)!, Name = Path.GetFileNameWithoutExtension(path),
                Category = categoryName, CandidateEntries = new() { path }, Preference = preference });
        }
        var unavailablePrefixes = new List<string>();
        foreach (var category in categories)
        {
            token.ThrowIfCancellationRequested();
            if (!TryAttributes(category, out var categoryAttributes))
            {
                unavailablePrefixes.Add(category);
                warnings.Add($"无法读取分类：{category}");
                continue;
            }
            if (!categoryAttributes.HasFlag(FileAttributes.Directory))
            { if (_settings.Layout == LibraryLayout.Direct) AddSingleFile(category, "未分类"); continue; }
            if (categoryAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                warnings.Add($"已跳过链接分类：{category}");
                continue;
            }

            bool categoryReadComplete = true;
            var appFolders = _settings.Layout == LibraryLayout.Direct ? new List<string> { category }
                : ReadEntries(category, warnings, token, out categoryReadComplete);
            if (!categoryReadComplete) unavailablePrefixes.Add(category);
            foreach (var folder in appFolders)
            {
                token.ThrowIfCancellationRequested();
                if (!TryAttributes(folder, out var attributes))
                {
                    unavailablePrefixes.Add(folder);
                    warnings.Add($"无法读取软件目录：{folder}");
                    continue;
                }
                if (!attributes.HasFlag(FileAttributes.Directory))
                { AddSingleFile(folder, Path.GetFileName(category)); continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    warnings.Add($"已跳过链接软件目录：{folder}");
                    continue;
                }
                if (!IsSafeSoftwareFolder(normalizedRoot, folder, _settings.Layout))
                {
                    unavailablePrefixes.Add(folder);
                    warnings.Add($"目录安全检查失败：{folder}");
                    continue;
                }

                string id = GetOrRememberIdentity(folder);
                AppPreference preference = GetPreference(id);
                preference.SingleFile = false;
                var appWarnings = new WarningList();
                List<string> candidates = FindEntries(folder, appWarnings, token, out bool complete);
                SanitizePreference(folder, preference, candidates, complete, appWarnings);
                if (preference.EntryRelativePath is null && complete && RecommendEntry(folder, candidates) is string recommended)
                    preference.EntryRelativePath = Path.GetRelativePath(folder, recommended);

                string? previousWarning = appWarnings.Items.Count == 0 ? null : string.Join("；", appWarnings.Items);
                var previous = _lastItems.FirstOrDefault(x => Paths.Equals(x.Id, id));
                items.Add(new SoftwareItem
                {
                    Id = id,
                    Layout = _settings.Layout,
                    Name = Path.GetFileName(folder),
                    Category = _settings.Layout == LibraryLayout.Direct ? "未分类" : Path.GetFileName(category),
                    FolderPath = folder,
                    CandidateEntries = candidates,
                    Preference = preference,
                    SizeBytes = previous?.SizeBytes,
                    SizeIncomplete = previous?.SizeIncomplete ?? false,
                    SizeCalculatedAt = previous?.SizeCalculatedAt,
                    DiscoveryWarning = previousWarning
                });
                foreach (string warning in appWarnings.Items) warnings.Add($"{Path.GetFileName(folder)}：{warning}");
            }
        }

        // An unreadable category must not make its previously known applications disappear.
        var seenIds = new HashSet<string>(items.Select(x => x.Id), Paths);
        foreach (var item in GetKnownItems(normalizedRoot, "该目录暂时无法读取，保留已知记录；入口和运行状态需要重新核实。"))
        {
            if (!seenIds.Contains(item.Id) && unavailablePrefixes.Any(prefix =>
                    Paths.Equals(prefix, item.StoragePath) || IsPathInside(prefix, item.StoragePath)))
            {
                items.Add(item);
                seenIds.Add(item.Id);
            }
        }

        items = items.OrderBy(x => x.Category, Paths).ThenBy(x => x.Name, Paths).ToList();
        // Keep app preferences, but discard path aliases proven absent by a completed scan.
        // This prevents an old rename alias from reappearing after a later access failure.
        var currentFolders = new HashSet<string>(items.Select(x => x.StoragePath), Paths);
        lock (_settings)
        {
            foreach (string oldPath in _settings.FolderIdentities.Keys.ToArray())
            {
                if (TryNormalize(oldPath, out string normalizedOldPath)
                    && HasLayoutDepth(normalizedRoot, normalizedOldPath, _settings.Layout)
                    && !currentFolders.Contains(normalizedOldPath)
                    && !unavailablePrefixes.Any(prefix => Paths.Equals(prefix, normalizedOldPath) || IsPathInside(prefix, normalizedOldPath)))
                    _settings.FolderIdentities.Remove(oldPath);
            }
        }
        _lastRoot = normalizedRoot;
        _lastLayout = _settings.Layout;
        _lastItems = items;
        return new(true, items, warnings.Items);
    }

    public FolderSizeResult CalculateSize(string folder, CancellationToken cancellationToken)
    {
        var calculatedAt = DateTime.Now;
        if (!TryNormalize(folder, out var normalized) || !HasNoReparseAncestors(normalized, true))
            return new(0, true, calculatedAt);

        long bytes = 0;
        bool incomplete = false;
        var pending = new Stack<string>();
        pending.Push(normalized);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasNoReparseAncestors(directory, true)) { incomplete = true; continue; }
            var entries = ReadEntries(directory, null, cancellationToken, out bool complete);
            incomplete |= !complete;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPathInside(normalized, entry) || !TryAttributes(entry, out var attributes))
                { incomplete = true; continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { incomplete = true; continue; }
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(entry);
                else
                {
                    try { bytes = checked(bytes + new FileInfo(entry).Length); }
                    catch (Exception ex) when (IsFileFailure(ex) || ex is OverflowException)
                    { incomplete = true; }
                }
            }
        }
        return new(bytes, incomplete, calculatedAt);
    }

    public FolderSizeResult CalculateItemSize(SoftwareItem item, CancellationToken cancellationToken)
    {
        if (!item.IsSingleFile) return CalculateSize(item.FolderPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!IsSafeEntryPath(item.FolderPath, item.SingleFilePath!)) return new(0, true, DateTime.Now);
            return new(new FileInfo(item.SingleFilePath!).Length, false, DateTime.Now);
        }
        catch (Exception ex) when (IsFileFailure(ex)) { return new(0, true, DateTime.Now); }
    }

    /// <summary>Lexical strict-child check. Does not establish filesystem/link safety by itself.</summary>
    public static bool IsPathInside(string parent, string child)
    {
        if (!TryNormalize(parent, out string normalizedParent) || !TryNormalize(child, out string normalizedChild))
            return false;
        string prefix = Path.EndsInDirectorySeparator(normalizedParent) ? normalizedParent : normalizedParent + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeSoftwareFolder(string root, string folder, LibraryLayout layout = LibraryLayout.Categorized)
    {
        if (LibraryRootProblem(root) is not null || !TryNormalize(root, out string normalizedRoot) || !TryNormalize(folder, out string normalizedFolder)
            || !HasLayoutDepth(normalizedRoot, normalizedFolder, layout)) return false;
        return HasNoReparseAncestors(normalizedRoot, true) && HasNoReparseAncestors(normalizedFolder, true);
    }

    public static bool IsSafeEntryPath(string folder, string entry)
    {
        return TryNormalize(folder, out string normalizedFolder) && TryNormalize(entry, out string normalizedEntry)
            && IsPathInside(normalizedFolder, normalizedEntry) && IsSupportedEntry(normalizedEntry)
            && HasNoReparseAncestors(normalizedFolder, true)
            && HasNoReparseAncestors(normalizedEntry, false);
    }

    public static bool IsCurrentSoftwareItem(string root, SoftwareItem item)
    {
        if (item.SingleFilePath is string file)
        {
            if (!IsSafeStandaloneFile(root, file, item.Layout) || !Paths.Equals(Path.GetDirectoryName(file), item.FolderPath)) return false;
            string? fileId = TryGetDirectoryId(file);
            return fileId is not null && Paths.Equals(fileId, item.Id);
        }
        if (!IsSafeSoftwareFolder(root, item.FolderPath, item.Layout)) return false;
        string? actualId = TryGetDirectoryId(item.FolderPath);
        return !item.Id.StartsWith("fs", StringComparison.OrdinalIgnoreCase)
            || (actualId is not null && string.Equals(actualId, item.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSafeStandaloneFile(string root, string file, LibraryLayout layout)
        => LibraryRootProblem(root) is null && TryNormalize(root, out string normalizedRoot) && TryNormalize(file, out string normalizedFile)
            && HasLayoutDepth(normalizedRoot, normalizedFile, layout)
            && Path.GetExtension(normalizedFile).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && IsSafeEntryPath(Path.GetDirectoryName(normalizedFile)!, normalizedFile);

    private static List<string> FindEntries(string folder, WarningList warnings, CancellationToken token, out bool complete)
    {
        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        complete = true;
        while (pending.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            if (!HasNoReparseAncestors(directory, true))
            {
                complete = false;
                warnings.Add($"无法读取目录或目录已变成链接：{Path.GetRelativePath(folder, directory)}");
                continue;
            }
            var entries = ReadEntries(directory, warnings, token, out bool readComplete);
            complete &= readComplete;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!IsPathInside(folder, entry) || !TryAttributes(entry, out var attributes))
                {
                    complete = false;
                    warnings.Add($"无法读取条目：{Path.GetRelativePath(folder, entry)}");
                    continue;
                }
                // Reparse entries are intentionally excluded, including links to paths inside this folder.
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                else if (IsSupportedEntry(entry) && IsSafeEntryPath(folder, entry)) candidates.Add(entry);
            }
        }
        return candidates.OrderBy(path => LooksLikeMaintenanceEntry(path) || LooksLikeAuxiliaryEntry(folder, path))
            .ThenBy(path => Path.GetRelativePath(folder, path).Count(c => c is '\\' or '/'))
            .ThenBy(path => path, Paths).ToList();
    }

    private static void SanitizePreference(string folder, AppPreference preference, List<string> candidates, bool complete, WarningList warnings)
    {
        lock (preference)
        {
            if (string.IsNullOrWhiteSpace(preference.EntryRelativePath)) preference.EntryRelativePath = null;
            if (!string.IsNullOrWhiteSpace(preference.EntryRelativePath))
            {
                string? entry = ResolveRelativeInside(folder, preference.EntryRelativePath, false);
                if (entry is null || !IsSupportedEntry(entry))
                {
                    preference.EntryRelativePath = null;
                    warnings.Add("保存的入口超出软件目录或不是支持的可执行文件，请重新选择。");
                }
                else if (!candidates.Contains(entry, Paths))
                {
                    // Missing entries are cleared only after an otherwise complete scan.
                    // A link, on the other hand, is always rejected even during a partial scan.
                    bool knownUnsafe = TryAttributes(entry, out var attrs) && attrs.HasFlag(FileAttributes.ReparsePoint);
                    if (complete || knownUnsafe)
                    {
                        preference.EntryRelativePath = null;
                        warnings.Add("保存的主程序已不存在或经过目录链接，请重新选择。");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(preference.WorkingDirectoryRelative)
                && ResolveRelativeInside(folder, preference.WorkingDirectoryRelative, true) is null)
            {
                preference.WorkingDirectoryRelative = null;
                warnings.Add("已清除超出软件目录的工作目录配置。");
            }
            preference.Arguments ??= "";
        }
    }

    private static string? ResolveRelativeInside(string folder, string relative, bool allowSame)
    {
        try
        {
            if (Path.IsPathRooted(relative)) return null;
            string combined = Path.GetFullPath(Path.Combine(folder, relative));
            return IsPathInside(folder, combined) || (allowSame && Paths.Equals(folder, Path.TrimEndingDirectorySeparator(combined)))
                ? combined : null;
        }
        catch (Exception ex) when (IsFileFailure(ex)) { return null; }
    }

    private string GetOrRememberIdentity(string folder)
    {
        string? filesystemId = TryGetDirectoryId(folder);
        lock (_settings)
        {
            _settings.FolderIdentities.TryGetValue(folder, out string? oldId);
            string id = filesystemId ?? oldId ?? "path:" + Guid.NewGuid().ToString("N");
            if (filesystemId is not null && oldId is not null && oldId.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
                && _settings.Apps.TryGetValue(oldId, out var oldPreference) && !_settings.Apps.ContainsKey(id))
                _settings.Apps[id] = oldPreference;
            _settings.FolderIdentities[folder] = id;
            return id;
        }
    }

    private AppPreference GetPreference(string id)
    {
        lock (_settings)
        {
            if (!_settings.Apps.TryGetValue(id, out AppPreference? preference) || preference is null)
                _settings.Apps[id] = preference = new AppPreference();
            return preference;
        }
    }

    private List<SoftwareItem> GetKnownItems(string root, string warning)
    {
        if (!TryNormalize(root, out string normalizedRoot)) return new();
        var known = new Dictionary<string, SoftwareItem>(Paths);
        if (Paths.Equals(_lastRoot, normalizedRoot) && _lastLayout == _settings.Layout)
        {
            foreach (var item in _lastItems)
                known[item.StoragePath] = new SoftwareItem
                {
                    Id = item.Id, Name = item.Name, Layout = item.Layout, SingleFilePath = item.SingleFilePath, Category = item.Category, FolderPath = item.FolderPath,
                    CandidateEntries = new List<string>(item.CandidateEntries), Preference = item.Preference,
                    SizeBytes = item.SizeBytes, SizeIncomplete = item.SizeIncomplete, SizeCalculatedAt = item.SizeCalculatedAt,
                    DiscoveryWarning = warning
                };
            // The latest completed scan is authoritative; do not revive old path aliases.
            return known.Values.ToList();
        }
        KeyValuePair<string, string>[] identities;
        lock (_settings) identities = _settings.FolderIdentities.ToArray();
        var knownIds = new HashSet<string>(known.Values.Select(x => x.Id), Paths);
        foreach (var pair in identities.Reverse())
        {
            if (!TryNormalize(pair.Key, out string folder) || !HasLayoutDepth(normalizedRoot, folder, _settings.Layout)
                || known.ContainsKey(folder) || !knownIds.Add(pair.Value)) continue;
            var preference = GetPreference(pair.Value);
            known[folder] = new SoftwareItem
            {
                Id = pair.Value,
                Layout = _settings.Layout,
                SingleFilePath = preference.SingleFile ? folder : null,
                Name = preference.SingleFile ? Path.GetFileNameWithoutExtension(folder) : Path.GetFileName(folder),
                Category = _settings.Layout == LibraryLayout.Direct ? "未分类" : Path.GetFileName(Path.GetDirectoryName(folder)) ?? "",
                FolderPath = preference.SingleFile ? Path.GetDirectoryName(folder)! : folder,
                Preference = preference,
                DiscoveryWarning = warning
            };
        }
        return known.Values.ToList();
    }

    private static bool HasLayoutDepth(string root, string folder, LibraryLayout layout)
    {
        if (!IsPathInside(root, folder)) return false;
        string relative = Path.GetRelativePath(root, folder);
        return Enum.IsDefined(layout) && relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length == (layout == LibraryLayout.Direct ? 1 : 2);
    }

    public static string? RecommendEntry(string folder, IReadOnlyList<string> candidates)
    {
        var programs = candidates.Where(path => !LooksLikeMaintenanceEntry(path) && !LooksLikeAuxiliaryEntry(folder, path)).ToArray();
        var topLevel = programs.Where(path => Paths.Equals(Path.GetDirectoryName(path), folder)).ToArray();
        if (topLevel.Length == 1) return topLevel[0];
        return programs.Length == 1 ? programs[0] : null;
    }

    private static bool LooksLikeAuxiliaryEntry(string folder, string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (new[] { "java", "javaw", "javaws", "dotnet", "crashpad_handler", "createdump", "update", "updater" }.Contains(name, Paths)) return true;
        return Path.GetRelativePath(folder, path).Split(new[] { '\\', '/' }).SkipLast(1)
            .Any(part => new[] { "runtime", "dotnet", "jre", "jvm", "redist" }.Contains(part, Paths));
    }

    private static bool IsSupportedEntry(string path) => Path.GetExtension(path) is string extension
        && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeMaintenanceEntry(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("setup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("install", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("installer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("卸载", StringComparison.Ordinal) || name.Contains("安装", StringComparison.Ordinal);
    }

    private static List<string> ReadEntries(string directory, WarningList? warnings, CancellationToken token, out bool complete)
    {
        var result = new List<string>();
        complete = true;
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) break;
                result.Add(enumerator.Current);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            complete = false;
            warnings?.Add($"无法完整读取 {directory}：{ex.Message}");
        }
        return result;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string candidate = path;
            if (candidate.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) candidate = "\\\\" + candidate[8..];
            else if (candidate.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) candidate = candidate[4..];
            if (candidate.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(candidate)) return false;
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            // Alternate data streams cannot be software paths.
            string? volumeRoot = Path.GetPathRoot(normalized);
            if (volumeRoot is null || normalized[volumeRoot.Length..].Contains(':')) return false;
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex)) { return false; }
    }

    private static bool HasNoReparseAncestors(string path, bool requireDirectory)
    {
        if (!TryNormalize(path, out var current)) return false;
        bool first = true;
        while (true)
        {
            if (!TryAttributes(current, out var attributes) || attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if ((requireDirectory || !first) && !attributes.HasFlag(FileAttributes.Directory)) return false;
            if (!requireDirectory && first && attributes.HasFlag(FileAttributes.Directory)) return false;
            first = false;
            string? parent;
            try { parent = Path.GetDirectoryName(current); }
            catch (Exception ex) when (IsFileFailure(ex)) { return false; }
            if (string.IsNullOrEmpty(parent) || Paths.Equals(parent, current)) return true;
            current = parent;
        }
    }

    private static bool TryAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception ex) when (IsFileFailure(ex)) { attributes = 0; return false; }
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string? TryGetDirectoryId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string nativePath = path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
            using var handle = CreateFileW(nativePath, 0, 1 | 2 | 4, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (GetFileInformationByHandleEx(handle, 18, out FileIdInformation information, (uint)Marshal.SizeOf<FileIdInformation>()))
                return $"fs:{information.VolumeSerialNumber:X16}:{information.FileIdHigh:X16}{information.FileIdLow:X16}";
            if (GetFileInformationByHandle(handle, out ByHandleFileInformation legacy))
                return $"fs64:{legacy.VolumeSerialNumber:X8}:{legacy.FileIndexHigh:X8}{legacy.FileIndexLow:X8}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    private sealed class WarningList
    {
        public List<string> Items { get; } = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public void Add(string message)
        {
            if (!_seen.Add(message)) return;
            if (Items.Count < 80) Items.Add(message);
            else if (Items.Count == 80) Items.Add("其他读取问题已省略。请检查相关目录的访问权限。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInformation information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
