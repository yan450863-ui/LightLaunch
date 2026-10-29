using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

public sealed record CleanupEligibility(bool Allowed,string Reason);
public sealed record CleanupEntry(ResidueFinding Finding,string Fingerprint);
public sealed class ResidueCleanupPlan
{
    internal ResidueCleanupPlan(ResidueSnapshot snapshot,IEnumerable<CleanupEntry> entries){Snapshot=snapshot;Entries=Array.AsReadOnly(entries.ToArray());}
    public ResidueSnapshot Snapshot{get;}
    public IReadOnlyList<CleanupEntry> Entries{get;}
    internal bool Used;
}
public sealed record CleanupOutcome(string Source,string Location,bool Success,string Message,string? BackupPath=null);
public sealed record ResidueCleanupResult(DateTimeOffset At,string SoftwareName,IReadOnlyList<CleanupOutcome> Outcomes,string RecordPath);

/// <summary>Selective cleanup of freshly validated candidates. The report itself never authorizes arbitrary deletion.</summary>
public sealed class ResidueCleanupService
{
    readonly string backupRoot;
    readonly ResidueService scanner=new();
    readonly WindowsResidueProbe probe=new();
    readonly HashSet<ResidueCleanupPlan> issued=new();
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    static readonly HashSet<string> Protected=new(StringComparer.OrdinalIgnoreCase){"Software","Microsoft","Windows","Classes","Clients","Policies","WOW6432Node","RegisteredApplications","CurrentVersion","Services","Installer","Uninstall","CLSID","AppID","TypeLib","Interface","Common Files","WindowsApps","dotnet","Program Files","Program Files (x86)","Users","AppData","ProgramData","Temp","Local","Roaming"};
    public ResidueCleanupService(string dataDirectory){backupRoot=Path.Combine(Path.GetFullPath(dataDirectory),"残留清理备份");}

    public static CleanupEligibility Eligibility(ResidueScanResult result,ResidueFinding finding)
    {
        if(result.Completion!=UninstallCompletionState.Completed)return new(false,"请先完成正规卸载并重新检测");
        if(finding.Source=="OriginalDirectory")return finding.Confidence==ResidueConfidence.VerifiedRemnant
            ?new(true,"移入回收站，包含目录内的数据"):new(false,"缺少卸载前相同目录身份的证据");
        if(finding.Source is "AppData Local" or "AppData Roaming" or "ProgramData")return new(true,"可能包含配置、个人或共享数据，需核对");
        if(finding.Source=="SoftwareRegistry") {
            try{var key=ParseRegistry(finding.Location);if(key.Path.Split('\\').Skip(1).Any(Protected.Contains))return new(false,"系统或公共注册表位置，保留");}
            catch(Exception){return new(false,"注册表位置无效");}
            return new(true,"先备份此精确键，再删除；可能含共享设置");
        }
        return new(false,"此类位置不能作为残留清除");
    }

    public ResidueCleanupPlan Prepare(ResidueScanResult displayed,IEnumerable<ResidueFinding> selected)
    {
        if(displayed.Completion!=UninstallCompletionState.Completed)throw new IOException("卸载尚未确认完成，不能清理残留。");
        var requested=selected.DistinctBy(f=>f.Source+"\0"+f.Location,StringComparer.OrdinalIgnoreCase).ToArray();
        if(requested.Length is 0 or >128)throw new IOException("请选择 1 到 128 项残留。");
        var fresh=scanner.Scan(displayed.Snapshot);EnsureUninstalled(fresh);
        EnsureNoOtherInstallation(fresh.Snapshot);
        var entries=new List<CleanupEntry>();
        foreach(var requestedItem in requested) {
            if(!displayed.Findings.Contains(requestedItem))throw new IOException("选择项不属于当前报告。");
            var actual=fresh.Findings.SingleOrDefault(f=>f.Source==requestedItem.Source&&f.Location.Equals(requestedItem.Location,StringComparison.OrdinalIgnoreCase))
                ??throw new IOException("残留位置已变化或不存在，请重新检测："+requestedItem.Location);
            var eligibility=Eligibility(fresh,actual);if(!eligibility.Allowed)throw new IOException(eligibility.Reason+"："+actual.Location);
            entries.Add(new(actual,Fingerprint(actual)));
        }
        // Selecting a parent folder already covers its children; never operate on the same tree twice.
        entries=entries.Where(e=>e.Finding.Source=="SoftwareRegistry"||!entries.Any(parent=>parent!=e&&parent.Finding.Source!="SoftwareRegistry"&&LibraryService.IsPathInside(parent.Finding.Location,e.Finding.Location))).ToList();
        var plan=new ResidueCleanupPlan(fresh.Snapshot,entries);lock(issued)issued.Add(plan);return plan;
    }
    public void Discard(ResidueCleanupPlan plan){lock(issued)issued.Remove(plan);}

    /// <summary>Caller must obtain explicit confirmation of these exact entries, including shared/user data, before execution.</summary>
    public ResidueCleanupResult Execute(ResidueCleanupPlan plan)
    {
        lock(issued){if(!issued.Remove(plan)||plan.Used)throw new IOException("清理预览已失效，请重新选择。");plan.Used=true;}
        EnsureUninstalled(scanner.Scan(plan.Snapshot));EnsureNoOtherInstallation(plan.Snapshot);
        ValidateBackupStorage();Directory.CreateDirectory(backupRoot);
        string batch=Path.Combine(backupRoot,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(batch);
        string recordPath=Path.Combine(batch,"清理记录.json");var outcomes=new List<CleanupOutcome>();
        void Save(){ValidateBackupStorage();string temporary=recordPath+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(new{At=DateTimeOffset.Now,SoftwareName=plan.Snapshot.SoftwareName,Entries=plan.Entries,Outcomes=outcomes},Json),new UTF8Encoding(false));File.Move(temporary,recordPath,true);}
        Save();
        foreach(var entry in plan.Entries) {
            int slot=outcomes.Count;outcomes.Add(new(entry.Finding.Source,entry.Finding.Location,false,"待核对并处理"));Save();
            string? backup=null;
            try {
                EnsureUninstalled(scanner.Scan(plan.Snapshot));EnsureNoOtherInstallation(plan.Snapshot);
                var current=scanner.Scan(plan.Snapshot);
                var actual=current.Findings.SingleOrDefault(f=>f.Source==entry.Finding.Source&&f.Location.Equals(entry.Finding.Location,StringComparison.OrdinalIgnoreCase));
                if(actual is null) {
                    var presence=entry.Finding.Source=="SoftwareRegistry"?RegistryPresence(entry.Finding.Location):probe.InspectPath(entry.Finding.Location).Presence;
                    if(presence==ResiduePresence.Absent){outcomes[slot]=new(entry.Finding.Source,entry.Finding.Location,true,"已不存在，无需重复清理");Save();continue;}
                    throw new IOException("此项不再属于当前候选，未处理。");
                }
                if(!Eligibility(current,actual).Allowed)throw new IOException("当前状态不再允许清理，未处理。");
                void Validate(){if(Fingerprint(entry.Finding)!=entry.Fingerprint)throw new IOException("确认后内容或身份已变化，保留此项，请重新检测。");}
                Validate();
                if(entry.Finding.Source=="SoftwareRegistry") {
                    var registry=ParseRegistry(entry.Finding.Location);
                    backup=Path.Combine(batch,$"{slot+1:D3}-{(registry.Hive==RegistryHive.CurrentUser?"HKCU":"HKLM")}-{(registry.View==RegistryView.Registry64?64:32)}.reg");
                    ExportRegistry(registry.Hive,registry.View,registry.Path,backup);
                    ValidateBackupStorage();Validate();outcomes[slot]=new(entry.Finding.Source,entry.Finding.Location,false,"已备份，等待确认删除结果",backup);Save();
                    File.WriteAllText(backup+".恢复说明.txt",$"原位置：{entry.Finding.Location}\r\n保留原注册表视图恢复，请关闭相关软件后运行：\r\n\"{Path.Combine(Environment.SystemDirectory,"reg.exe")}\" import \"{backup}\" /reg:{(registry.View==RegistryView.Registry64?64:32)}\r\n",new UTF8Encoding(false));
                    DeleteRegistryTree(registry.Hive,registry.View,registry.Path);
                    if(probe.InspectRegistry(registry.Hive,registry.View,registry.Path).Presence!=ResiduePresence.Absent)throw new IOException("系统未确认注册表项已移除。");
                }else UninstallService.RecycleResidue(entry.Finding.Location,Validate);
                outcomes[slot]=new(entry.Finding.Source,entry.Finding.Location,true,backup is null?"已移入回收站":"已备份并删除精确注册表项",backup);
            }catch(Exception ex){outcomes[slot]=new(entry.Finding.Source,entry.Finding.Location,false,"未完成："+ex.Message,backup);}
            Save();
        }
        return new(DateTimeOffset.Now,plan.Snapshot.SoftwareName,outcomes.AsReadOnly(),recordPath);
    }

    static void EnsureUninstalled(ResidueScanResult result)
    {
        if(result.Completion!=UninstallCompletionState.Completed||!result.Checks.Any(c=>c.Source=="OriginalRegistration"&&c.Presence==ResiduePresence.Absent)
            ||!result.Checks.Any(c=>c.Source=="OriginalMainExecutable"&&c.Presence==ResiduePresence.Absent))throw new IOException("主程序或原登记尚未确认移除，未清理残留。");
    }
    static void EnsureNoOtherInstallation(ResidueSnapshot snapshot)
    {
        var records=new UninstallService().GetRegistrations();
        if(records is not UninstallRegistrationList{ReadComplete:true})throw new IOException("已安装产品读取不完整，未清理残留。");
        if(records.Any(r=>r.Id!=snapshot.Registration.Id&&(r.DisplayName.Equals(snapshot.Registration.DisplayName,StringComparison.OrdinalIgnoreCase)
            ||r.DisplayName.Equals(snapshot.SoftwareName,StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(r.InstallLocation)&&PathEquals(r.InstallLocation,snapshot.OriginalFolderPath)))))
            throw new IOException("存在同名或同安装位置的其他已安装产品，保留可能共享的数据。");
    }
    string Fingerprint(ResidueFinding finding)=>finding.Source=="SoftwareRegistry"?RegistryFingerprint(finding.Location):FileFingerprint(finding.Location);
    ResiduePresence RegistryPresence(string location){var key=ParseRegistry(location);return probe.InspectRegistry(key.Hive,key.View,key.Path).Presence;}
    string FileFingerprint(string path)
    {
        path=DirectoryJunction.LocalPath(path);
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows),data=Path.GetDirectoryName(backupRoot)!;
        if(Protected.Contains(Path.GetFileName(path))||PathEquals(path,windows)||LibraryService.IsPathInside(windows,path)
            ||PathEquals(path,data)||LibraryService.IsPathInside(data,path)||LibraryService.IsPathInside(path,data)
            ||PathEquals(path,AppContext.BaseDirectory)||LibraryService.IsPathInside(AppContext.BaseDirectory,path)||LibraryService.IsPathInside(path,AppContext.BaseDirectory))
            throw new IOException("系统或管理器自身目录不能清除。");
        foreach(string shared in new[]{Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps")})
            if(!string.IsNullOrWhiteSpace(shared)&&(PathEquals(path,shared)||LibraryService.IsPathInside(shared,path)))throw new IOException("公共系统组件目录不能清除。");
        var state=probe.InspectPath(path);if(state.Presence!=ResiduePresence.Present)throw new IOException("位置不可读取或包含链接。");
        foreach(var process in Process.GetProcesses())using(process){try{string? exe=process.MainModule?.FileName;if(exe is not null&&(PathEquals(path,exe)||LibraryService.IsPathInside(path,exe)))throw new IOException("此目录仍有程序运行，请先退出。");}catch(Win32Exception){}catch(InvalidOperationException){}}
        var pending=new Stack<string>();pending.Push(path);var values=new List<string>();
        while(pending.TryPop(out var current)) {
            if(values.Count>=100000)throw new IOException("文件数量过多，当前不自动清理。");
            var attributes=File.GetAttributes(current);if(attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("目录内包含链接，未清理。");
            using var handle=CreateFile(current,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var info))throw new IOException("无法核实文件身份。");
            values.Add(current+"|"+info.Volume+"|"+info.IndexHigh+"|"+info.IndexLow+"|"+info.SizeHigh+"|"+info.SizeLow+"|"+info.Write.dwHighDateTime+"|"+info.Write.dwLowDateTime);
            if(attributes.HasFlag(FileAttributes.Directory))foreach(string child in Directory.EnumerateFileSystemEntries(current))pending.Push(child);
        }
        return Hash(values);
    }
    string RegistryFingerprint(string location)
    {
        var r=ParseRegistry(location);using var root=RegistryKey.OpenBaseKey(r.Hive,r.View);
        var pending=new Stack<string>();pending.Push(r.Path);var values=new List<string>();long bytes=0;int keys=0;
        while(pending.TryPop(out var path)) {
            if(++keys>10000||path.Count(c=>c=='\\')>64)throw new IOException("注册表项过大，未自动清理。");
            if(probe.InspectRegistry(r.Hive,r.View,path).Presence!=ResiduePresence.Present)throw new IOException("注册表项经过链接或不能完整读取。");
            using var key=root.OpenSubKey(path)??throw new IOException("注册表项已变化。");
            int queried=RegQueryInfoKey(key.Handle,null,IntPtr.Zero,IntPtr.Zero,out _,out _,out _,out _,out _,out _,out _,out long modified);
            if(queried!=0)throw new Win32Exception(queried);
            values.Add(path+"|"+modified);
            foreach(string name in key.GetValueNames()) {
                var kind=key.GetValueKind(name);if(kind==RegistryValueKind.Unknown)throw new IOException("包含不支持备份的注册表类型。");
                var value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)??throw new IOException("不能完整读取注册表值。");
                string serialized=JsonSerializer.Serialize(new{Path=path,Name=name,Kind=kind,Value=value});bytes+=serialized.Length*2;
                if(bytes>16*1024*1024)throw new IOException("注册表备份内容过大，未处理。");values.Add(serialized);
            }
            foreach(string child in key.GetSubKeyNames())pending.Push(path+"\\"+child);
        }
        return Hash(values);
    }
    void DeleteRegistryTree(RegistryHive hive,RegistryView view,string path)
    {
        using var root=RegistryKey.OpenBaseKey(hive,view);
        var pending=new Stack<string>();pending.Push(path);var nodes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string Values(RegistryKey key)=>Hash(key.GetValueNames().Select(name=>JsonSerializer.Serialize(new{Name=name,Kind=key.GetValueKind(name),Value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)})));
        while(pending.TryPop(out var current)) {
            if(nodes.Count>=10000||probe.InspectRegistry(hive,view,current).Presence!=ResiduePresence.Present)throw new IOException("清理前注册表结构已变化，未继续。");
            using var key=root.OpenSubKey(current)??throw new IOException("注册表项已经变化。");
            nodes.Add(current,Values(key));foreach(string child in key.GetSubKeyNames())pending.Push(current+"\\"+child);
        }
        // Non-recursive deletes cannot absorb an unexpected new subtree appearing after confirmation.
        foreach(var node in nodes.OrderByDescending(pair=>pair.Key.Count(c=>c=='\\'))) {
            if(probe.InspectRegistry(hive,view,node.Key).Presence!=ResiduePresence.Present)throw new IOException("注册表项出现链接或状态变化，停止清理；备份保留。");
            using(var key=root.OpenSubKey(node.Key)??throw new IOException("注册表项已变化。")) {
                if(key.SubKeyCount!=0||Values(key)!=node.Value)throw new IOException("注册表内容已变化，停止后续清理；已备份，可恢复部分处理的项目。");
            }
            // Delete the exact child through the hive handle; do not request write access to the broad Software parent.
            root.DeleteSubKey(node.Key,true);
        }
    }
    static (RegistryHive Hive,RegistryView View,string Path) ParseRegistry(string location)
    {
        var match=Regex.Match(location,@"^HK(?<h>CU|LM) \[(?<v>32|64)\]\\(?<p>Software\\[^\r\n\0]+)$",RegexOptions.IgnoreCase);
        if(!match.Success)throw new IOException("注册表位置不明确。");
        string path=match.Groups["p"].Value;var segments=path.Split('\\');
        if(segments.Length is <2 or >3||segments.Skip(1).Any(s=>string.IsNullOrWhiteSpace(s)||s is "." or ".."||Protected.Contains(s)))throw new IOException("只允许软件专属的一级或两级注册表路径。");
        return(match.Groups["h"].Value.Equals("CU",StringComparison.OrdinalIgnoreCase)?RegistryHive.CurrentUser:RegistryHive.LocalMachine,
            match.Groups["v"].Value=="64"?RegistryView.Registry64:RegistryView.Registry32,path);
    }
    static void ExportRegistry(RegistryHive hive,RegistryView view,string path,string backup)
    {
        // The executable's bitness selects the same view as the validated key. No command interpreter is used.
        string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),view==RegistryView.Registry32?"SysWOW64":"System32","reg.exe");
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("export");info.ArgumentList.Add((hive==RegistryHive.CurrentUser?"HKCU":"HKLM")+"\\"+path);info.ArgumentList.Add(backup);
        using var process=Process.Start(info)??throw new IOException("无法启动注册表备份。");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();process.WaitForExit();Task.WaitAll(output,error);
        if(process.ExitCode!=0||!File.Exists(backup)||new System.IO.FileInfo(backup).Length<32)throw new IOException("注册表备份失败，原项未删除。"+error.Result.Trim());
    }
    void ValidateBackupStorage()
    {
        foreach(var path in new[]{backupRoot})for(var d=new DirectoryInfo(path);d is not null;d=d.Parent){try{if(File.GetAttributes(d.FullName).HasFlag(FileAttributes.ReparsePoint))throw new IOException("清理备份路径经过链接，未执行。");}catch(DirectoryNotFoundException){}catch(FileNotFoundException){}}
    }
    static string Hash(IEnumerable<string> values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",values.Order(StringComparer.Ordinal)))));
    static bool PathEquals(string first,string second){try{return DirectoryJunction.LocalPath(first).Equals(DirectoryJunction.LocalPath(second),StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}}
    [StructLayout(LayoutKind.Sequential)]struct FileInfo {public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateFileW",SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo information);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegQueryInfoKeyW")]static extern int RegQueryInfoKey(SafeRegistryHandle key,StringBuilder? @class,IntPtr classSize,IntPtr reserved,out uint subkeys,out uint maxSubkey,out uint maxClass,out uint values,out uint maxValueName,out uint maxValueData,out uint security,out long lastWrite);
}
