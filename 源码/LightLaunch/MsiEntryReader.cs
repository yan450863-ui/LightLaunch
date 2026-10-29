using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LightLaunch;

public sealed record MsiEntryEvidence(string OriginalPath,long Size,string Version);

/// <summary>Reads cached MSI tables and registered component paths. Never configures or repairs a product.</summary>
public static class MsiEntryReader
{
    public static MsiEntryEvidence? Find(UninstallRegistration registration,string entry)
    {
        var match=Regex.Match(registration.Command,@"^\s*(?:\""[^\""\r\n]*msiexec(?:\.exe)?\""|msiexec(?:\.exe)?)\s+/[ix]\s*(?<id>\{[0-9a-f-]{36}\})\s*$",RegexOptions.IgnoreCase);
        if(!match.Success||!Guid.TryParse(match.Groups["id"].Value,out var product))return null;
        var id=registration.Id.Split('|');if(id.Length!=3||!Guid.TryParse(id[2],out var registered)||registered!=product)return null;
        string code=product.ToString("B");var package=new StringBuilder(32768);uint length=(uint)package.Capacity;
        if(MsiGetProductInfo(code,"LocalPackage",package,ref length)!=0)return null;
        string path=package.ToString();if(new WindowsResidueProbe().InspectPath(path).Presence!=ResiduePresence.Present)return null;
        if(MsiOpenDatabase(path,IntPtr.Zero,out uint database)!=0)return null;
        try {
            var files=Rows(database,"SELECT `Component_`, `FileName`, `FileSize`, `Version` FROM `File`",4)
                .Where(row=>row[1].Split('|').Last().Equals(Path.GetFileName(entry),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(files.Length!=1)return null;
            var components=Rows(database,"SELECT `Component`, `ComponentId` FROM `Component`",2);
            var component=components.SingleOrDefault(row=>row[0]==files[0][0]);if(component is null||!Guid.TryParse(component[1],out _))return null;
            var installed=new StringBuilder(32768);uint size=(uint)installed.Capacity;
            int state=MsiGetComponentPath(code,component[1],installed,ref size);
            if(state<0||state==0||size>=installed.Capacity)return null;
            string original=DirectoryJunction.LocalPath(installed.ToString());
            if(!Path.GetFileName(original).Equals(Path.GetFileName(entry),StringComparison.OrdinalIgnoreCase)||!long.TryParse(files[0][2],out long bytes))return null;
            return new(original,bytes,files[0][3]);
        }catch(Exception ex)when(ex is ArgumentException or IOException or InvalidOperationException){return null;}
        finally{MsiCloseHandle(database);}
    }
    static List<string[]> Rows(uint database,string query,int columns)
    {
        if(MsiDatabaseOpenView(database,query,out uint view)!=0)throw new IOException("MSI 缓存表不可读取。");
        try {
            if(MsiViewExecute(view,0)!=0)throw new IOException("MSI 缓存查询失败。");
            var rows=new List<string[]>();
            while(true) {
                uint error=MsiViewFetch(view,out uint record);if(error==259)break;if(error!=0)throw new IOException("MSI 缓存读取不完整。");
                try {
                    if(rows.Count>=10000)throw new IOException("MSI 缓存超出限定范围。");
                    var row=new string[columns];for(uint i=1;i<=columns;i++){var value=new StringBuilder(32768);uint n=(uint)value.Capacity;if(MsiRecordGetString(record,i,value,ref n)!=0)throw new IOException("MSI 字段不可读取。");row[i-1]=value.ToString();}rows.Add(row);
                }finally{MsiCloseHandle(record);}
            }
            return rows;
        }finally{MsiCloseHandle(view);}
    }
    [DllImport("msi.dll",CharSet=CharSet.Unicode,EntryPoint="MsiGetProductInfoW")]static extern uint MsiGetProductInfo(string product,string property,StringBuilder value,ref uint length);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,EntryPoint="MsiGetComponentPathW")]static extern int MsiGetComponentPath(string product,string component,StringBuilder path,ref uint length);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,EntryPoint="MsiOpenDatabaseW")]static extern uint MsiOpenDatabase(string path,IntPtr mode,out uint database);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,EntryPoint="MsiDatabaseOpenViewW")]static extern uint MsiDatabaseOpenView(uint database,string query,out uint view);
    [DllImport("msi.dll")]static extern uint MsiViewExecute(uint view,uint record);
    [DllImport("msi.dll")]static extern uint MsiViewFetch(uint view,out uint record);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,EntryPoint="MsiRecordGetStringW")]static extern uint MsiRecordGetString(uint record,uint field,StringBuilder value,ref uint length);
    [DllImport("msi.dll")]static extern uint MsiCloseHandle(uint handle);
}
