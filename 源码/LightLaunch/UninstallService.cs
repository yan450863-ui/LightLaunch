using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LightLaunch;

public sealed class UninstallService
{
    public static RegistryView[] SoftwareViews(RegistryHive hive, string path)
    {
        bool redirected = new[] { @"Software\Classes\CLSID", @"Software\Classes\DirectShow", @"Software\Classes\Interface", @"Software\Classes\Media Type", @"Software\Classes\MediaFoundation" }
            .Any(prefix => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
        return hive == RegistryHive.CurrentUser && !redirected ? new[] { RegistryView.Registry64 } : new[] { RegistryView.Registry64, RegistryView.Registry32 };
    }
    public static bool SameRegistrationId(string? left, string? right)
    {
        static string? Canonical(string? id) => id?.StartsWith("CurrentUser|Registry32|", StringComparison.Ordinal) == true
            ? "CurrentUser|Registry64|" + id["CurrentUser|Registry32|".Length..] : id;
        return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
    }
    public static bool SameRegistration(UninstallRegistration left, UninstallRegistration right)
        => SameRegistrationId(left.Id, right.Id) && (left with { Id = right.Id }) == right;

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public List<UninstallRegistration> GetRegistrations()
    {
        var registrations = new List<UninstallRegistration>();
        bool complete = true;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in SoftwareViews(hive, UninstallKey))
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null) { complete = false; continue; }
                        var registration = ReadRegistration(key, MakeId(hive, view, name));
                        if (registration is not null) registrations.Add(registration);
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { complete = false; }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { complete = false; }
        }
        // Preserve exact registry IDs: an otherwise identical entry in another hive/view must not erase a saved choice.
        return new UninstallRegistrationList(registrations.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase), complete);
    }

    /// <summary>Suggestions for a user picker; a suggestion never changes the software kind or saved association.</summary>
    public List<UninstallRegistration> FindCandidates(SoftwareItem item)
        => FindCandidates(item, GetRegistrations());

    public List<UninstallRegistration> FindCandidates(SoftwareItem item, IEnumerable<UninstallRegistration> registrations)
    {
        var name = NormalizeName(item.Name);
        return registrations.Select(r => (Registration: r, Score: CandidateScore(item, name, r)))
            .Where(candidate => candidate.Score > 0).OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Registration.DisplayName).Select(candidate => candidate.Registration).ToList();
    }

    /// <summary>Returns a dialog default only. Never edits preferences or assumes an unregistered app is portable.</summary>
    public static UninstallProposal ProposeDefault(SoftwareItem item, IEnumerable<UninstallRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(registrations);
        var values = registrations.ToList();
        if (item.Preference.Kind == AppKind.Portable) return new(AppKind.Portable, null, false);
        if (!string.IsNullOrWhiteSpace(item.Preference.UninstallRegistryId))
        {
            var saved = values.FirstOrDefault(value => SameRegistrationId(value.Id, item.Preference.UninstallRegistryId));
            return new(saved is not null ? AppKind.Installed : item.Preference.Kind, saved, false,
                saved is null ? "原先选择的卸载记录已不存在，请重新核对选择。" : null);
        }
        if (item.Preference.Kind == AppKind.Installed) return new(AppKind.Installed, null, false);
        if (registrations is UninstallRegistrationList { ReadComplete: false })
            return new(AppKind.Unknown, null, false, "部分 Windows 卸载记录无法读取，请手动核对软件类型。");

        var groups = values.Select(value => (Registration: value, Evidence: StrongEvidence(item, value)))
            .Where(value => value.Evidence > 0)
            .GroupBy(value => EquivalenceKey(value.Registration)).ToList();
        if (groups.Count != 1)
            return new(AppKind.Unknown, null, false, groups.Count > 1 ? "发现多条关联卸载记录，请手动核对对象。" : null);
        var selected = groups[0].OrderByDescending(value => value.Evidence).ThenBy(value => value.Registration.Id, StringComparer.Ordinal)
            .First().Registration;
        try { _ = PrepareUninstallCommand(selected); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return new(AppKind.Unknown, null, false, "关联记录的卸载命令不可用，请手动核对或使用 Windows 已安装的应用。");
        }
        return new(AppKind.Installed, selected, true, "已根据 Windows 卸载记录识别为安装版，请核对后确认。");
    }

    public void LaunchRegisteredUninstaller(UninstallRegistration registration)
    {
        using var process = BeginRegisteredUninstall(registration);
    }

    /// <summary>The caller owns the process handle. Exit alone never proves that removal succeeded.</summary>
    public Process? BeginRegisteredUninstall(UninstallRegistration registration)
    {
        var (executable, arguments) = ValidateRegisteredCommand(registration);
        var start = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        return Process.Start(start);
    }

    public TrackedUninstall BeginTrackedUninstall(UninstallRegistration registration)
    {
        var (executable, arguments) = ValidateRegisteredCommand(registration);
        return TrackedUninstall.Start(executable, arguments);
    }

    private static (string Executable, string Arguments) ValidateRegisteredCommand(UninstallRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        // Never execute a command cached when the settings dialog was opened: reread the exact registry id.
        var current = ReadRegistrationById(registration.Id)
            ?? throw new InvalidOperationException("这条卸载登记已不存在，请重新选择或打开 Windows 已安装的应用。");
        if (!current.Command.Equals(registration.Command, StringComparison.Ordinal)
            || !current.DisplayName.Equals(registration.DisplayName, StringComparison.Ordinal)
            || !string.Equals(current.InstallLocation, registration.InstallLocation, StringComparison.Ordinal)
            || !string.Equals(current.DisplayIcon, registration.DisplayIcon, StringComparison.Ordinal))
            throw new InvalidOperationException("卸载登记已经变化，请重新选择并核对后再执行。");
        return PrepareUninstallCommand(current);
        // The registered uninstaller owns removal. The manager never deletes its installation directory.
    }

    private static (string Executable, string Arguments) PrepareUninstallCommand(UninstallRegistration registration)
    {
        var (executable, arguments) = ParseCommand(registration.Command);
        var fileName = Path.GetFileName(executable);
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            // /I is maintenance, not uninstall. Always use the registered MSI product code with interactive /x.
            var product = Regex.Match(arguments, @"^/[ix]\s*(?<code>\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\s*$", RegexOptions.IgnoreCase);
            if (!product.Success || !Guid.TryParse(product.Groups["code"].Value, out var productCode))
                throw new InvalidOperationException("MSI 登记必须是单一 /I 或 /X 产品代码；补丁或复杂命令请在 Windows 已安装的应用中处理。");
            var id = registration.Id.Split('|', 3);
            if (id.Length == 3 && Guid.TryParse(id[2], out var registeredCode) && registeredCode != productCode)
                throw new InvalidOperationException("MSI 命令与卸载登记的产品代码不一致，请重新核对。");
            executable = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            arguments = "/x " + productCode.ToString("B");
        }
        else
        {
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
                || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("卸载程序路径不明确或不可用，请在 Windows 已安装的应用中卸载。");
            if (new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe" }
                .Contains(fileName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("该登记需要脚本解释器，首版请使用 Windows 已安装的应用进行卸载。");
            if (Regex.IsMatch(arguments, @"(?:^|\s)[/-](?:s|silent|verysilent|quiet|passive|qn|qb\S*)(?=\s|$)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("卸载登记包含静默参数；请在 Windows 已安装的应用中交互卸载。");
        }
        return (executable, arguments);
    }

    internal static bool CanUseDetectedRegistration(UninstallRegistration registration,SoftwareItem item,string? original)
    {
        try {
            var candidate=registration;
            var (executable,arguments)=ParseCommand(registration.Command);
            if(original is not null&&Path.IsPathFullyQualified(executable)&&LibraryService.IsPathInside(original,executable)&&!File.Exists(executable)) {
                string relocated=Path.GetFullPath(Path.Combine(item.FolderPath,Path.GetRelativePath(original,executable)));
                if(!LibraryService.IsSafeEntryPath(item.FolderPath,relocated))return false;
                candidate=registration with{Command="\""+relocated+"\" "+arguments};
            }
            _=PrepareUninstallCommand(candidate);return true;
        }catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException){return false;}
    }

    public void RecyclePortable(string libraryRoot, SoftwareItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Preference.Kind != AppKind.Portable)
            throw new InvalidOperationException("只有明确标记为便携软件的项目才能移入回收站；未识别到卸载程序不代表便携版。");
        if (item.IsSingleFile) { RecycleSingleFile(libraryRoot, item); return; }
        var folder = Path.GetFullPath(item.FolderPath);
        var snapshot = new SoftwareItem { Id = item.Id, FolderPath = folder, Layout = item.Layout };
        if (!LibraryService.IsCurrentSoftwareItem(libraryRoot, snapshot))
            throw new InvalidOperationException("软件文件夹已经移动或被替换，请刷新列表后重新核对。");
        ValidateRecycleTree(libraryRoot, folder, snapshot.Layout);
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RecycleOnSta(libraryRoot, snapshot);
            return;
        }
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { RecycleOnSta(libraryRoot, snapshot); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "LightLaunch Recycle Bin" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void RecycleSingleFile(string libraryRoot, SoftwareItem item)
    {
        if (item.SingleFilePath is not string path || !LibraryService.IsCurrentSoftwareItem(libraryRoot, item))
            throw new InvalidOperationException("单 EXE 已移动、被替换或不在当前软件目录中，本次未删除。");
        var snapshot = new SoftwareItem { Id = item.Id, Layout = item.Layout, SingleFilePath = Path.GetFullPath(path), FolderPath = Path.GetFullPath(item.FolderPath) };
        RecycleResidue(snapshot.SingleFilePath, () => {
            if (!LibraryService.IsCurrentSoftwareItem(libraryRoot, snapshot)) throw new IOException("单 EXE 在确认后已变化，本次未删除。");
        });
    }

    public void OpenWindowsAppsSettings()
    {
        using var process = Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
    }

    private static UninstallRegistration? ReadRegistration(RegistryKey? key, string id)
    {
        if (key is null || Convert.ToString(key.GetValue("SystemComponent")) == "1") return null;
        var name = Convert.ToString(key.GetValue("DisplayName"))?.Trim();
        var command = Convert.ToString(key.GetValue("UninstallString"))?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command)) return null;
        return new(id, name, Convert.ToString(key.GetValue("InstallLocation"))?.Trim().Trim('"'),
            command, Convert.ToString(key.GetValue("Publisher"))?.Trim(), Convert.ToString(key.GetValue("DisplayIcon"))?.Trim());
    }

    private static string MakeId(RegistryHive hive, RegistryView view, string name)
        => $"{hive}|{view}|{name}";

    private static UninstallRegistration? ReadRegistrationById(string id)
    {
        var fields = id.Split('|', 3);
        if (fields.Length != 3 || !Enum.TryParse<RegistryHive>(fields[0], out var hive)
            || !Enum.TryParse<RegistryView>(fields[1], out var view)
            || hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine)
            || view is not (RegistryView.Registry32 or RegistryView.Registry64)
            || string.IsNullOrWhiteSpace(fields[2]) || fields[2].Contains('\\'))
            throw new InvalidOperationException("卸载登记标识无效，请重新选择。");
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(UninstallKey + "\\" + fields[2]);
        return ReadRegistration(key, id);
    }

    private static int CandidateScore(SoftwareItem item, string normalizedName, UninstallRegistration registration)
    {
        var score = 0;
        try
        {
            if (!string.IsNullOrWhiteSpace(registration.InstallLocation) && Path.IsPathFullyQualified(registration.InstallLocation))
            {
                var folder = Path.GetFullPath(item.FolderPath).TrimEnd('\\');
                var install = Path.GetFullPath(registration.InstallLocation).TrimEnd('\\');
                if (folder.Equals(install, StringComparison.OrdinalIgnoreCase)) score += 100;
                // A drive, library or category ancestor is not evidence that this registration owns the app.
                else if (LibraryService.IsPathInside(folder, install)) score += 60;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        score = Math.Max(score, StrongEvidence(item, registration));
        var registeredName = NormalizeName(registration.DisplayName);
        if (normalizedName.Length > 0 && normalizedName == registeredName) score += 40;
        else if (normalizedName.Length >= 3 && registeredName.Length >= 3
            && (registeredName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains(registeredName, StringComparison.OrdinalIgnoreCase))) score += 15;
        return score;
    }

    private static string NormalizeName(string value) => Regex.Replace(value, @"[\s\p{P}\p{S}]+", "").ToUpperInvariant();

    private static int StrongEvidence(SoftwareItem item, UninstallRegistration registration)
    {
        try
        {
            int evidence;
            if (!string.IsNullOrWhiteSpace(registration.InstallLocation))
                evidence = SameAbsolutePath(item.FolderPath, registration.InstallLocation) ? 100 : 0;
            else
            {
                string? icon = IconExecutable(registration.DisplayIcon);
                if (icon is not null && LibraryService.IsSafeEntryPath(item.FolderPath, icon)) evidence = 80;
                else
                {
                    var (executable, _) = ParseCommand(registration.Command);
                    evidence = Path.IsPathFullyQualified(executable) && LibraryService.IsSafeEntryPath(item.FolderPath, executable) ? 70 : 0;
                }
            }
            // Ownership ambiguity is independent of whether an uninstaller is currently usable.
            // Only validate the command after one product remains; a broken second product must not disappear.
            return evidence;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException) { return 0; }
    }

    private static string? IconExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string path = value.Trim();
        if (path.Contains('\0') || path.Contains('\r') || path.Contains('\n')) return null;
        if (path.StartsWith('"'))
        {
            int end = path.IndexOf('"', 1);
            if (end <= 1 || !Regex.IsMatch(path[(end + 1)..], @"^\s*(?:,\s*-?\d+\s*)?$")) return null;
            path = path[1..end];
        }
        else path = Regex.Replace(path, @",\s*-?\d+\s*$", "").Trim();
        return Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool SameAbsolutePath(string first, string second)
    {
        return Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
            && Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    }

    private static string EquivalenceKey(UninstallRegistration registration)
    {
        var fields = registration.Id.Split('|', 3);
        // Only shared 32/64-bit views of one exact hive/subkey can be mirrors; never merge separate products or users.
        string source = fields.Length == 3 ? fields[0] + "|" + fields[2] : registration.Id;
        string location = string.IsNullOrWhiteSpace(registration.InstallLocation) ? ""
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(registration.InstallLocation));
        return string.Join('\0', source.ToUpperInvariant(), location.ToUpperInvariant(), registration.Command,
            registration.DisplayIcon ?? "", registration.DisplayName, registration.Publisher ?? "");
    }

    private static (string Executable, string Arguments) ParseCommand(string value)
    {
        // RegistryKey.GetValue already expands REG_EXPAND_SZ. REG_SZ may contain literal '%' in a path.
        var command = value.Trim();
        if (command.Length == 0 || command.Contains('\0') || command.Contains('\r') || command.Contains('\n'))
            throw new InvalidOperationException("卸载命令无效。");
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            if (end <= 1) throw new InvalidOperationException("卸载命令的路径引号不完整。");
            return (command[1..end], command[(end + 1)..].TrimStart());
        }
        // Resolve the .exe boundary ourselves: an unquoted Program Files path must never execute C:\Program.exe.
        var executableEnd = Regex.Match(command, @"\.(?:exe|com)(?=\s|$)", RegexOptions.IgnoreCase);
        if (executableEnd.Success)
        {
            var end = executableEnd.Index + executableEnd.Length;
            return (command[..end], command[end..].TrimStart());
        }
        var separator = command.IndexOfAny(new[] { ' ', '\t' });
        return separator < 0 ? (command, "") : (command[..separator], command[(separator + 1)..].TrimStart());
    }

    private static void ValidateRecycleTree(string libraryRoot, string folder, LibraryLayout layout)
    {
        if (!LibraryService.IsSafeSoftwareFolder(libraryRoot, folder, layout))
            throw new InvalidOperationException("只允许回收当前目录模式下的软件文件夹；软件库根目录、目录链接和越界路径已拒绝。");
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("软件目录中存在目录链接、符号链接或其他重解析点；为保护链接目标，首版不自动回收此目录。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        }
    }

    private static void RecycleOnSta(string libraryRoot, SoftwareItem itemSnapshot)
    {
        var folder = itemSnapshot.FolderPath;
        RecycleExactOnSta(folder,()=>{
            ValidateRecycleTree(libraryRoot,folder,itemSnapshot.Layout);
            if(!LibraryService.IsCurrentSoftwareItem(libraryRoot,itemSnapshot))throw new InvalidOperationException("软件文件夹在确认后被替换，回收操作已取消。请刷新列表。");
        });
    }

    // Only the residual cleanup service supplies this validation after bounding and fingerprinting the selected candidate.
    internal static void RecycleResidue(string path,Action validate)
    {
        if(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA){RecycleExactOnSta(path,validate);return;}
        Exception? failure=null;
        var worker=new Thread(()=>{try{RecycleExactOnSta(path,validate);}catch(Exception ex){failure=ex;}}){IsBackground=true,Name="LightLaunch Residue Recycle"};
        worker.SetApartmentState(ApartmentState.STA);worker.Start();worker.Join();
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RecycleExactOnSta(string folder,Action validate)
    {
        // Revalidate immediately before the shell operation, including the complete descendant tree.
        validate();
        var recycleInfo = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        var recycleQuery = SHQueryRecycleBin(Path.GetPathRoot(folder)!, ref recycleInfo);
        if (recycleQuery < 0)
            throw new IOException($"此磁盘的回收站当前不可用（0x{recycleQuery:X8}），已拒绝删除软件文件夹。");
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out item));
            // Win11 recycle-only flag, stop on error, and a sink that cancels any non-recycling deletion.
            const uint flags = 0x00080000 | 0x20000000 | 0x00100000 | 0x0400 | 0x0004 | 0x0040 | 0x0010;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            var sink = new RecycleOnlySink();
            validate();
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
            var result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            if (sink.PermanentDeleteBlocked)
                throw new IOException("当前目录无法安全移入回收站，已拒绝永久删除。");
            if (result < 0) throw new IOException("移入回收站失败；未尝试永久删除。", Marshal.GetExceptionForHR(result));
            if (aborted) throw new OperationCanceledException("移入回收站已取消；未尝试永久删除。");
            if (Directory.Exists(folder)||File.Exists(folder)) throw new IOException("系统未完成回收，请检查占用或回收站设置。");
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo { public uint Size; public long Bytes, Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW", PreserveSig = true)]
    private static extern int SHQueryRecycleBin(string rootPath, ref RecycleBinInfo information);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        public bool PermanentDeleteBlocked { get; private set; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & 0x80) != 0) return 0; // TSF_DELETE_RECYCLE_IF_POSSIBLE
            PermanentDeleteBlocked = true;
            return unchecked((int)0x80004004); // E_ABORT, never approve a permanent-delete fallback.
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? newItem) => 0;
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string templateName, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
