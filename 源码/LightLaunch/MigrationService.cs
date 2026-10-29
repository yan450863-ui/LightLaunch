using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace LightLaunch;

public sealed record MigrationPlan(string LibraryRoot,string SoftwareId,string TargetPath,string OriginalPath,string MainRelativePath,UninstallRegistration Registration);
public sealed record MigrationRecord(Guid Id,string LibraryRoot,string SoftwareId,string TargetPath,string OriginalPath,string MainRelativePath,
    UninstallRegistration Registration,string? JunctionIdentity,string State,DateTimeOffset CreatedAt);

/// <summary>Associates an already cut, real software folder with its old install path. Does not move data or rewrite registry entries.</summary>
public sealed class MigrationService
{
    readonly string recordsRoot;
    readonly Func<List<UninstallRegistration>> readRegistrations;
    static readonly JsonSerializerOptions Json = new(){WriteIndented=true,PropertyNameCaseInsensitive=true,MaxDepth=24};
    public MigrationService(string dataDirectory):this(dataDirectory,()=>new UninstallService().GetRegistrations()) { }
    public MigrationService(string dataDirectory,Func<List<UninstallRegistration>> readRegistrations)
    { recordsRoot=Path.Combine(Path.GetFullPath(dataDirectory),"迁入记录");this.readRegistrations=readRegistrations??throw new ArgumentNullException(nameof(readRegistrations)); }

    public static string? SuggestOriginalDirectory(UninstallRegistration registration)
    {
        if(!string.IsNullOrWhiteSpace(registration.InstallLocation)) {
            try{return DirectoryJunction.LocalPath(registration.InstallLocation);}catch(ArgumentException){}
        }
        foreach(var path in ReferencedExecutables(registration)) {
            if(!Path.GetFileName(path).Equals("msiexec.exe",StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(path);
        }
        return null;
    }
    public MigrationPlan Prepare(string root,SoftwareItem item,UninstallRegistration registration,string originalPath)
    {
        root=DirectoryJunction.LocalPath(root);
        if(!LibraryService.IsCurrentSoftwareItem(root,item) || item.EntryPath is null || !LibraryService.IsSafeEntryPath(item.FolderPath,item.EntryPath))
            throw new IOException("迁入软件目录或主入口无效，请刷新列表并选择主程序。");
        string target=DirectoryJunction.LocalPath(item.FolderPath),original=DirectoryJunction.LocalPath(originalPath);
        ValidateOriginal(root,original,target);
        if(!Directory.Exists(Path.GetDirectoryName(original))) throw new DirectoryNotFoundException("原安装位置的父目录不存在，当前不会新建整串旧路径。");
        if(File.Exists(original) || Directory.Exists(original) || EntryExists(original)) throw new IOException("原位置仍被占用。请完整剪切软件文件夹，不能保留原副本或认领已有链接。");
        if(!string.IsNullOrWhiteSpace(registration.InstallLocation)) {
            if(!Same(original,registration.InstallLocation)) throw new IOException("原目录与所选登记的安装位置不一致。");
        }
        var registry=readRegistrations();
        if(registry is not UninstallRegistrationList {ReadComplete:true}) throw new IOException("卸载登记读取不完整，迁入关联未执行。");
        var current=registry.SingleOrDefault(r=>r.Id==registration.Id);
        if(current is null || current!=registration) throw new IOException("卸载登记已经变化，请重新选择。");
        if(string.IsNullOrWhiteSpace(registration.InstallLocation)&&!ReferencedExecutables(registration).Any(path=>LibraryService.IsPathInside(original,path))) {
            var detected=new SoftwareDetectionService().Detect(item,registry);
            if(detected.Proposal.Registration?.Id!=registration.Id||detected.OriginalDirectory is null||!Same(original,detected.OriginalDirectory))
                throw new IOException("该登记缺少可靠的原目录证据，不能自动提供原路径。");
        }
        foreach(var other in registry) {
            if(other.Id==registration.Id || Mirror(other,registration)) continue;
            if((!string.IsNullOrWhiteSpace(other.InstallLocation) && (Same(original,other.InstallLocation)||LibraryService.IsPathInside(original,other.InstallLocation)))
                || ReferencedExecutables(other).Any(path=>LibraryService.IsPathInside(original,path)))
                throw new IOException("原目录还关联其他软件（"+other.DisplayName+"），当前不迁入共享安装目录。");
        }
        EnsureNoLinkedDescendants(target);
        EnsureNotRunning(target,original);
        EnsureNoServices(original);
        if(GetForItem(item) is not null) throw new IOException("此项目已经有迁入记录，请先核对现有兼容入口；当前不重复建立。");
        return new(root,item.Id,target,original,Path.GetRelativePath(target,item.EntryPath),registration);
    }
    public MigrationRecord Commit(MigrationPlan plan,bool keepCompatibility=false)
    {
        var item=new SoftwareItem {Id=plan.SoftwareId,FolderPath=plan.TargetPath,Name=Path.GetFileName(plan.TargetPath),
            Preference=new AppPreference{EntryRelativePath=plan.MainRelativePath}};
        var checkedPlan=Prepare(plan.LibraryRoot,item,plan.Registration,plan.OriginalPath);
        var record=new MigrationRecord(Guid.NewGuid(),checkedPlan.LibraryRoot,checkedPlan.SoftwareId,checkedPlan.TargetPath,checkedPlan.OriginalPath,
            checkedPlan.MainRelativePath,checkedPlan.Registration,null,"Prepared",DateTimeOffset.Now);
        if(!keepCompatibility){record=record with{State="Detached"};Write(record);return record;}
        Write(record); // Durable intent before changing the old path; a crash leaves a recoverable record.
        JunctionInfo? created=null;
        try {
            created=DirectoryJunction.Create(record.OriginalPath,record.TargetPath);
            if(!Same(created.Target,record.TargetPath)) throw new IOException("新兼容入口的目标未通过核验。");
            record=record with {JunctionIdentity=created.Identity,State="Ready"};Write(record);return record;
        } catch {
            if(created is not null) {
                try {DirectoryJunction.RemoveOwned(record.OriginalPath,record.TargetPath,created.Identity);record=record with{State="Failed"};Write(record);} catch { /* Keep durable intent and actual link for inspection; never delete its target. */ }
            }
            throw;
        }
    }
    public MigrationRecord? GetForItem(SoftwareItem item)
    {
        if(!Directory.Exists(recordsRoot))return null;
        ValidateStoragePath();
        MigrationRecord? found=null;
        string[] files=Directory.EnumerateFiles(recordsRoot,"*.json").Take(10001).ToArray();
        if(files.Length>10000)throw new IOException("迁入记录过多，请先核对数据目录。");
        foreach(string file in files) {
            if(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))throw new IOException("迁入记录经过文件链接，未读取。");
            if(new FileInfo(file).Length>1024*1024)throw new IOException("迁入记录异常，请先核对数据目录。");
            var record=JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(file),Json) ?? throw new IOException("迁入记录无效。");
            if(record.State is "Completed" or "Failed")continue;
            if(record.SoftwareId!=item.Id)continue;
            if(found is not null)throw new IOException("此软件存在重复迁入记录，需要先核对。");
            found=record;
        }
        return found;
    }
    public MigrationRecord? RecoverInterrupted(string root,SoftwareItem item)
    {
        var record=GetForItem(item);
        if(record?.State=="TemporaryPrepared") {
            ValidateRecord(root,item,record);
            var temporary=DirectoryJunction.Read(record.OriginalPath);
            if(temporary is null){record=record with{State="Detached",JunctionIdentity=null};Write(record);return record;}
            if(!Same(temporary.Target,record.TargetPath))throw new IOException("中断的临时入口已被改变，未处理。");
            record=record with{State="TemporaryActive",JunctionIdentity=temporary.Identity};Write(record);return record;
        }
        if(record is null || record.State!="Prepared")return record;
        ValidateRecord(root,item,record);
        var link=DirectoryJunction.Read(record.OriginalPath);
        if(link is null){Write(record with{State="Failed"});return null;}
        if(!Same(link.Target,record.TargetPath))throw new IOException("中断记录的旧兼容入口指向其他位置，未认领或修改。");
        var registrations=readRegistrations();
        if(registrations is not UninstallRegistrationList{ReadComplete:true} || !registrations.Contains(record.Registration))throw new IOException("原登记已变化，请先人工核对中断的迁入记录。");
        var recovered=record with{JunctionIdentity=link.Identity,State="Ready"};Write(recovered);return recovered;
    }
    public void ValidateForUninstall(string root,SoftwareItem item,UninstallRegistration registration)
    {
        var record=GetForItem(item);if(record is null)return;
        ValidateRecord(root,item,record);
        if(record.Registration!=registration)throw new IOException("所选卸载产品与迁入记录不同或原登记已变化，请重新核对。");
        EnsureNoLinkedDescendants(record.TargetPath);
        EnsureNotRunning(record.TargetPath,record.OriginalPath);
        EnsureNoServices(record.OriginalPath);
        var link=DirectoryJunction.Read(record.OriginalPath);
        if(record.State=="Detached") {
            if(link is not null)throw new IOException("原位置出现其他入口，未覆盖或认领。");
            return;
        }
        if(record.State!="Ready" || link is null || link.Identity!=record.JunctionIdentity || !Same(link.Target,item.FolderPath))
            throw new IOException("旧路径兼容入口已变化或迁入尚未完成；为保护其他安装位置，本次不启动卸载器。");
    }
    /// <returns>True when a temporary link was created; false for an explicitly retained compatibility link.</returns>
    public bool BeginUninstallCompatibility(string root,SoftwareItem item,UninstallRegistration registration)
    {
        ValidateForUninstall(root,item,registration);
        var record=GetForItem(item);if(record is null||record.State=="Ready")return false;
        var registrations=readRegistrations();
        if(registrations is not UninstallRegistrationList{ReadComplete:true}||!registrations.Contains(registration))throw new IOException("原卸载登记已经变化，未建立临时入口。");
        EnsureNotRunning(record.TargetPath,record.OriginalPath);
        record=record with{State="TemporaryPrepared"};Write(record);
        var link=DirectoryJunction.Create(record.OriginalPath,record.TargetPath);
        Write(record with{State="TemporaryActive",JunctionIdentity=link.Identity});return true;
    }
    /// <summary>For interrupted temporary links the caller must explicitly confirm that the uninstaller has ended.
    /// Only the recorded junction is removed; the real software directory is never deleted.</summary>
    public void RemoveCompatibility(string root,SoftwareItem item,bool uninstallerEnded=false)
    {
        var record=GetForItem(item)??throw new IOException("没有对应迁入记录。");
        if(record.State=="Detached")return;
        if(record.State!="Ready" && !(uninstallerEnded&&record.State=="TemporaryActive"))throw new IOException("请先确认卸载器及其子进程已经结束，再清理临时入口。");
        if(!Same(root,record.LibraryRoot)||item.Id!=record.SoftwareId||!Same(item.FolderPath,record.TargetPath))throw new IOException("迁入路径与记录不一致，未修改入口。");
        ValidateOriginal(root,record.OriginalPath,record.TargetPath,true);
        if(Directory.Exists(record.TargetPath)&&!LibraryService.IsCurrentSoftwareItem(root,item))throw new IOException("真实目录身份已变化，未修改入口。");
        EnsureNotRunning(record.TargetPath,record.OriginalPath);
        if(record.JunctionIdentity is null)throw new IOException("缺少入口身份，未处理旧位置。");
        DirectoryJunction.RemoveOwned(record.OriginalPath,record.TargetPath,record.JunctionIdentity);
        Write(record with{State="Detached",JunctionIdentity=null});
    }
    public bool CanRecycleRemainder(string root,SoftwareItem item)
    {
        var record=GetForItem(item);if(record is null)return false;
        if(record.State is not ("Ready" or "Detached"))throw new IOException("卸载记录尚未完成，请再次点击卸载核对临时入口。");
        ValidateRecord(root,item,record,true);
        var scan=ScanCurrent(item,record);
        return scan.Checks.Any(c=>c.Source=="OriginalRegistration" && c.Presence==ResiduePresence.Absent);
    }
    public void RecycleRemainder(string root,SoftwareItem item)
    {
        if(!CanRecycleRemainder(root,item))throw new IOException("尚未确认原产品登记已移除，不能清理迁入目录。");
        var record=GetForItem(item)!;EnsureNotRunning(item.FolderPath,record.OriginalPath);
        // Explicit post-uninstall folder recycling; does not change the saved Installed classification.
        var folder=new SoftwareItem{Id=item.Id,FolderPath=item.FolderPath,Preference=new AppPreference{Kind=AppKind.Portable}};
        new UninstallService().RecyclePortable(root,folder);
    }
    public string FinishUninstall(string root,SoftwareItem item)
    {
        var record=GetForItem(item);if(record is null)return "";
        if(record.State is not ("Ready" or "Detached"))throw new IOException("迁入记录尚未完成，未清理目录或兼容入口。");
        if(!Same(root,record.LibraryRoot)||!Same(item.FolderPath,record.TargetPath))throw new IOException("软件库或迁入路径与记录不一致，请先恢复迁入时的目录位置。");
        ValidateOriginal(root,record.OriginalPath,record.TargetPath,true);
        var scan=ScanCurrent(item,record);
        bool removed=scan.Checks.Any(c=>c.Source=="OriginalRegistration"&&c.Presence==ResiduePresence.Absent)
            && scan.Checks.Any(c=>c.Source=="OriginalMainExecutable"&&c.Presence==ResiduePresence.Absent);
        if(!removed)return "迁入软件仍有主程序或登记，请查看检测结果。";
        if(Directory.Exists(record.TargetPath)) {
            if(!LibraryService.IsCurrentSoftwareItem(root,item))throw new IOException("迁入目录身份已改变，未处理目录或兼容入口。");
            if(Directory.EnumerateFileSystemEntries(record.TargetPath).Any())return "迁入目录仍有数据，已保留文件夹，请查看检测结果。";
        } else if(EntryExists(record.TargetPath))throw new IOException("迁入目标被替换，未处理兼容入口。");
        if(record.State=="Ready") {
            if(record.JunctionIdentity is null)throw new IOException("缺少兼容入口身份，未自动移除旧位置。");
            DirectoryJunction.RemoveOwned(record.OriginalPath,record.TargetPath,record.JunctionIdentity);
        }
        // Non-recursive: only an empty, still identical software folder can be removed.
        if(Directory.Exists(record.TargetPath)) {
            if(!LibraryService.IsCurrentSoftwareItem(root,item))throw new IOException("目录身份已改变，保留空目录。");
            Directory.Delete(record.TargetPath,false);
        }
        Write(record with{State="Completed"});return "迁入主程序和登记已移除，已清理空目录及本管理器建立的旧兼容入口。";
    }
    static ResidueScanResult ScanCurrent(SoftwareItem item,MigrationRecord record)
        =>new ResidueService().Scan(ResidueSnapshot.Create(item.Name,record.TargetPath,Path.Combine(record.TargetPath,record.MainRelativePath),record.Registration));
    static void ValidateRecord(string root,SoftwareItem item,MigrationRecord record,bool allowMissingOriginal=false)
    {
        if(!Same(root,record.LibraryRoot) || !LibraryService.IsCurrentSoftwareItem(root,item) || item.Id!=record.SoftwareId || !Same(item.FolderPath,record.TargetPath))
            throw new IOException("迁入软件目录或软件库已变化，当前不修改旧兼容入口。");
        ValidateOriginal(root,record.OriginalPath,record.TargetPath,allowMissingOriginal);
    }
    void Write(MigrationRecord record)
    {
        ValidateStoragePath();
        Directory.CreateDirectory(recordsRoot);
        string path=Path.Combine(recordsRoot,record.Id.ToString("N")+".json"),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(stream,record,Json);
        File.Move(temporary,path,true);
    }
    void ValidateStoragePath()
    {
        for(var path=new DirectoryInfo(recordsRoot);path is not null;path=path.Parent) {
            try {if(File.GetAttributes(path.FullName).HasFlag(FileAttributes.ReparsePoint))throw new IOException("迁入记录目录经过链接，当前不写入或读取。");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
    }
    static bool EntryExists(string path) {try{_=File.GetAttributes(path);return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}}
    static bool Same(string first,string second) {try{return DirectoryJunction.LocalPath(first).Equals(DirectoryJunction.LocalPath(second),StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}}
    static bool Mirror(UninstallRegistration a,UninstallRegistration b)
    {
        string[] x=a.Id.Split('|'),y=b.Id.Split('|');return x.Length==3&&y.Length==3&&x[0]==y[0]&&x[2]==y[2]&&a.Command==b.Command&&a.DisplayName==b.DisplayName;
    }
    static IEnumerable<string> ReferencedExecutables(UninstallRegistration registration)
    {
        foreach(string? raw in new[]{registration.DisplayIcon,registration.Command}) {
            if(string.IsNullOrWhiteSpace(raw))continue;
            string value=raw.Trim();int end=value.StartsWith('"')?value.IndexOf('"',1):-1;
            if(end>1)value=value[1..end];
            else {int exe=value.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(exe>=0)value=value[..(exe+4)];}
            if(!Path.GetExtension(value).Equals(".exe",StringComparison.OrdinalIgnoreCase))continue;
            string? normalized=null;try{normalized=DirectoryJunction.LocalPath(value);}catch(ArgumentException){}
            if(normalized is not null)yield return normalized;
        }
    }
    static void ValidateOriginal(string root,string original,string target,bool allowMissingOriginal=false)
    {
        original=DirectoryJunction.LocalPath(original);target=DirectoryJunction.LocalPath(target);
        if(Same(original,target)||Same(original,root)||LibraryService.IsPathInside(root,original)||LibraryService.IsPathInside(original,target)||LibraryService.IsPathInside(target,original))
            throw new IOException("原位置必须在软件库外，不能与软件库或目标目录重叠。");
        var reserved=new[]{Path.GetPathRoot(original),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};
        if(reserved.Any(p=>!string.IsNullOrWhiteSpace(p)&&Same(original,p)))throw new IOException("不能使用磁盘根、用户根或公共目录作为原软件位置。");
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if(Same(original,windows)||LibraryService.IsPathInside(windows,original)||new[]{"Common Files","WindowsApps","Microsoft","dotnet","Windows Kits"}.Contains(Path.GetFileName(original),StringComparer.OrdinalIgnoreCase))
            throw new IOException("当前不支持系统组件或公共共享目录迁入。");
        foreach(string path in new[]{Path.GetDirectoryName(original)!,Directory.Exists(target)?target:Path.GetDirectoryName(target)!}) {
            for(var d=new DirectoryInfo(path);d is not null;d=d.Parent) {
                FileAttributes attributes;
                try{attributes=File.GetAttributes(d.FullName);}
                catch(Exception error)when(allowMissingOriginal&&Same(path,Path.GetDirectoryName(original)!)&&error is FileNotFoundException or DirectoryNotFoundException){continue;}
                if(attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("迁入路径经过已有目录链接，当前不处理。");
                if(!attributes.HasFlag(FileAttributes.Directory))throw new IOException("迁入路径的上级不是目录。");
            }
        }
    }
    static void EnsureNoLinkedDescendants(string path)
    {
        var pending=new Stack<string>();pending.Push(path);
        while(pending.TryPop(out string? dir))foreach(string entry in Directory.EnumerateFileSystemEntries(dir)) {
            var attributes=File.GetAttributes(entry);
            if(attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("迁入目录包含链接，当前不建立旧路径兼容入口。");
            if(attributes.HasFlag(FileAttributes.Directory))pending.Push(entry);
        }
    }
    static void EnsureNotRunning(string target,string original)
    {
        foreach(var process in Process.GetProcesses())using(process) {
            string? path;try{path=process.MainModule?.FileName;}catch(System.ComponentModel.Win32Exception){continue;}catch(InvalidOperationException){continue;}
            if(path is not null&&(LibraryService.IsPathInside(target,path)||LibraryService.IsPathInside(original,path)))throw new IOException("软件目录中仍有程序运行，请先正常退出后再关联或卸载。");
        }
    }
    static void EnsureNoServices(string original)
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services") ?? throw new IOException("不能核对系统服务，当前不建立迁入关联。");
        foreach(string name in key.GetSubKeyNames()) {
            using var service=key.OpenSubKey(name);string? image=Convert.ToString(service?.GetValue("ImagePath"));
            if(image is not null&&Environment.ExpandEnvironmentVariables(image).Contains(original+"\\",StringComparison.OrdinalIgnoreCase))
                throw new IOException("原目录关联服务或驱动（"+name+"），当前版本不自动迁入。");
        }
    }
}
