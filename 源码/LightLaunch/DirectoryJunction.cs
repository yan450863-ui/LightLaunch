using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

public sealed record JunctionInfo(string Target, string Identity);

/// <summary>Only NTFS mount-point junctions; never recursive traversal or recursive removal.</summary>
public static class DirectoryJunction
{
    const uint MountPoint = 0xA0000003;
    public static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("需要本地磁盘绝对路径。");
        string? root=Path.GetPathRoot(path);
        if (root is null || root.Length!=3 || root[1]!=':' || path[2]!='\\' || path[3..].Contains(':')) throw new ArgumentException("不支持网络、设备或数据流路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static JunctionInfo? Read(string path)
    {
        path=LocalPath(path);
        FileAttributes attributes;
        try { attributes=File.GetAttributes(path); }
        catch(FileNotFoundException){return null;} catch(DirectoryNotFoundException){return null;}
        if (!attributes.HasFlag(FileAttributes.Directory) || !attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("旧位置已存在普通文件或目录，不能替换。");
        using var handle=Open(path,0);
        byte[] data=new byte[16384];
        if(!DeviceIoControl(handle,0x000900A8,null,0,data,data.Length,out int count,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if(count<16 || BinaryPrimitives.ReadUInt32LittleEndian(data)!=MountPoint) throw new IOException("旧位置不是本功能支持的目录联接。");
        int size=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        int offset=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        int length=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        if(size<8 || size+8>count || (offset|length)%2!=0 || offset+length>size-8) throw new IOException("目录联接数据无效。");
        string value=Encoding.Unicode.GetString(data,16+offset,length);
        if(!value.StartsWith(@"\??\",StringComparison.Ordinal)) throw new IOException("目录联接目标不是本地路径。");
        return new(LocalPath(value[4..]),Identity(handle));
    }
    public static JunctionInfo Create(string path,string target)
    {
        path=LocalPath(path);target=LocalPath(target);
        if(!Directory.Exists(target) || !Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("目标或原安装位置的父目录不存在。");
        if(!CreateDirectory(path,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"原位置已被占用或不能建立兼容入口。");
        bool set=false;
        try {
            using(var handle=Open(path,0x40000000)) {
                byte[] substitute=Encoding.Unicode.GetBytes(@"\??\"+target),print=Encoding.Unicode.GetBytes(target);
                byte[] data=new byte[16+substitute.Length+2+print.Length+2];
                BinaryPrimitives.WriteUInt32LittleEndian(data,MountPoint);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4),checked((ushort)(data.Length-8)));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10),checked((ushort)substitute.Length));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12),checked((ushort)(substitute.Length+2)));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14),checked((ushort)print.Length));
                substitute.CopyTo(data,16);print.CopyTo(data,18+substitute.Length);
                if(!DeviceIoControl(handle,0x000900A4,data,data.Length,null,0,out _,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"建立旧路径兼容入口失败。");
                set=true;
            }
            return Read(path) ?? throw new IOException("未能核实兼容入口。");
        } catch {
            // RemoveDirectory never follows a junction and never recursively removes directory contents.
            if(!set) RemoveDirectory(path);
            throw;
        }
    }
    public static bool RemoveOwned(string path,string expectedTarget,string expectedIdentity)
    {
        var current=Read(path);if(current is null)return false;
        if(!current.Target.Equals(LocalPath(expectedTarget),StringComparison.OrdinalIgnoreCase) || current.Identity!=expectedIdentity)
            throw new IOException("旧兼容入口已改变，已保留，未处理其他位置。");
        if(!RemoveDirectory(LocalPath(path))) throw new Win32Exception(Marshal.GetLastWin32Error(),"兼容入口未能移除；没有递归删除其目标。");
        return true;
    }
    static SafeFileHandle Open(string path,uint access)
    {
        var handle=CreateFile(path,access,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
        return handle;
    }
    static string Identity(SafeFileHandle handle)
    {
        byte[] id=new byte[24];
        if(!GetFileInformationByHandleEx(handle,18,id,id.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Convert.ToHexString(id);
    }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="CreateDirectoryW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CreateDirectory(string path,IntPtr security);
    [DllImport("kernel32.dll",EntryPoint="RemoveDirectoryW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool RemoveDirectory(string path);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int information,byte[] data,int size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[]? input,int inputSize,byte[]? output,int outputSize,out int returned,IntPtr overlapped);
}
