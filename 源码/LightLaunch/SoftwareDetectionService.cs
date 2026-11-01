using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LightLaunch;

public sealed record SoftwareDetection(UninstallProposal Proposal,IReadOnlyList<UninstallRegistration> Candidates,string? OriginalDirectory=null);
public sealed class SoftwareDetectionService
{
    readonly Func<UninstallRegistration,string,MsiEntryEvidence?> readMsi;
    public SoftwareDetectionService():this(MsiEntryReader.Find){}
    public SoftwareDetectionService(Func<UninstallRegistration,string,MsiEntryEvidence?> readMsi){this.readMsi=readMsi;}
    public SoftwareDetection Detect(SoftwareItem item,List<UninstallRegistration> records,MigrationRecord? migration=null)
    {
        var candidates=new UninstallService().FindCandidates(item,records);
        if(records is not UninstallRegistrationList{ReadComplete:true})return new(new(AppKind.Unknown,null,false,"卸载记录未完整读取，暂不推断软件类型。"),candidates);
        var fresh=new SoftwareItem{Name=item.Name,FolderPath=item.FolderPath,Preference=new AppPreference{EntryRelativePath=item.Preference.EntryRelativePath},CandidateEntries=item.CandidateEntries};
        var direct=UninstallService.ProposeDefault(fresh,records);
        if(direct.Kind==AppKind.Installed)return new(direct,candidates);
        if(migration is not null) {
            var saved=records.SingleOrDefault(r=>UninstallService.SameRegistrationId(r.Id,migration.Registration.Id));
            if(saved is not null)return new(new(AppKind.Installed,saved,true,"已根据此文件夹的原产品关联识别为安装版。"),new[]{saved},migration.OriginalPath);
            return new(new(AppKind.Unknown,null,false,"原产品登记已移除，请核对剩余文件。"),Array.Empty<UninstallRegistration>());
        }
        var entries=(item.EntryPath is{} main&&Path.GetExtension(main).Equals(".exe",StringComparison.OrdinalIgnoreCase)?new[]{main}:item.CandidateEntries.Where(p=>!LibraryService.LooksLikeMaintenanceEntry(p)).Take(16)).Where(p=>Path.GetExtension(p).Equals(".exe",StringComparison.OrdinalIgnoreCase)&&LibraryService.IsSafeEntryPath(item.FolderPath,p)).ToArray();
        var matches=new List<(UninstallRegistration Record,string? Original)>();
        foreach(string entry in entries) {
            var version=FileVersionInfo.GetVersionInfo(entry);
            foreach(var record in records) {
                bool product=SameProduct(version.ProductName,record.DisplayName);
                if(!product&&!SameProduct(Path.GetFileNameWithoutExtension(entry),record.DisplayName))continue;
                if(!candidates.Any(c=>c.Id==record.Id))candidates.Add(record);
                var msi=readMsi(record,entry);
                if(msi is not null) {
                    bool same=Path.GetFullPath(msi.OriginalPath).Equals(entry,StringComparison.OrdinalIgnoreCase);
                    bool moved=!File.Exists(msi.OriginalPath)&&product&&msi.Size==new FileInfo(entry).Length&&VersionMatches(msi.Version,version);
                    if(same||moved){matches.Add((record,same?null:Path.GetDirectoryName(msi.OriginalPath)));continue;}
                }
                string? original=MigrationService.SuggestOriginalDirectory(record);
                if(original is not null&&!Directory.Exists(original)&&product&&SameVendor(version.CompanyName,record.Publisher)
                    &&record.DisplayIcon is{} icon&&icon.Contains(Path.GetFileName(entry),StringComparison.OrdinalIgnoreCase))matches.Add((record,original));
            }
        }
        var groups=matches.GroupBy(m=>MirrorKey(m.Record)).ToArray();
        if(groups.Length==1) {
            var found=groups[0].First();
            if(!UninstallService.CanUseDetectedRegistration(found.Record,item,found.Original))return new(new(AppKind.Unknown,null,false,"已发现匹配产品，但正规卸载命令不可用，请核对。"),new[]{found.Record});
            return new(new(AppKind.Installed,found.Record,true,"已根据程序信息及安装记录识别为安装版；卸载时自动处理已剪切的原路径。"),new[]{found.Record},found.Original);
        }
        if(groups.Length>1||direct.Reason?.Contains("多条")==true)return new(new(AppKind.Unknown,null,false,"发现多个匹配产品，请在当前软件的候选中核对。"),candidates.Concat(matches.Select(m=>m.Record)).DistinctBy(r=>r.Id).ToArray());
        if(!string.IsNullOrWhiteSpace(item.Preference.UninstallRegistryId)) {
            var saved=records.SingleOrDefault(r=>UninstallService.SameRegistrationId(r.Id,item.Preference.UninstallRegistryId));
            if(saved is not null)return new(new(AppKind.Installed,saved,false,"使用此软件已保存的卸载关联，请核对。"),new[]{saved},MigrationService.SuggestOriginalDirectory(saved));
            return new(new(AppKind.Unknown,null,false,"原先保存的卸载登记已不存在，请核对当前软件的处理方式。"),candidates);
        }
        if(item.Preference.Kind==AppKind.Installed)return new(new(AppKind.Unknown,null,false,"此项目原先标记为安装版，但当前未找到可用登记，请核对。"),candidates);
        if(candidates.Count>0||item.CandidateEntries.Any(LibraryService.LooksLikeMaintenanceEntry))return new(new(AppKind.Unknown,null,false,"存在安装或卸载线索，但尚不能唯一对应；请核对方式，避免把安装版直接回收。"),candidates);
        return new(new(AppKind.Portable,null,true,"未发现与此软件匹配的 Windows 卸载登记，建议按文件夹移入回收站；这不能证明它是便携版，确认前请核对目录内的数据。"),Array.Empty<UninstallRegistration>());
    }
    static string Canon(string? value)=>Regex.Replace(Regex.Replace(Regex.Replace(value??"",@"\([^)]*(?:bit|x64|x86|arm64)[^)]*\)","",RegexOptions.IgnoreCase),@"\b(?:v?\d+(?:\.\d+){1,4}|x64|x86|arm64|64-bit|32-bit)\b","",RegexOptions.IgnoreCase),@"[\s\p{P}\p{S}]+","").ToUpperInvariant();
    static bool SameProduct(string? a,string? b)=>Canon(a).Length>=3&&Canon(a)==Canon(b);
    static bool SameVendor(string? a,string? b)=>SameProduct(Regex.Replace(a??"",@"\b(?:Team|Inc|Ltd|LLC|Corporation)\b","",RegexOptions.IgnoreCase),Regex.Replace(b??"",@"\b(?:Team|Inc|Ltd|LLC|Corporation)\b","",RegexOptions.IgnoreCase));
    static bool VersionMatches(string expected,FileVersionInfo version)=>Version.TryParse(expected,out var e)&&e.Major==version.FileMajorPart&&e.Minor==version.FileMinorPart&&Math.Max(e.Build,0)==version.FileBuildPart&&Math.Max(e.Revision,0)==version.FilePrivatePart;
    static string MirrorKey(UninstallRegistration r){var id=r.Id.Split('|',3);return string.Join('|',id.Length==3?id[0]+"|"+id[2]:r.Id,r.DisplayName,r.Command,r.InstallLocation,r.DisplayIcon,r.Publisher);}
}
