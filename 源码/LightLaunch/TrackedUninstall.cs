using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

/// <summary>A suspended start assigns the uninstaller and its descendants to one job before any code runs.
/// Closing these handles never terminates the uninstaller.</summary>
public sealed class TrackedUninstall : IDisposable
{
    readonly SafeProcessHandle process;
    readonly SafeFileHandle job;
    TrackedUninstall(SafeProcessHandle process,SafeFileHandle job){this.process=process;this.job=job;}
    internal static TrackedUninstall Start(string executable,string arguments)
    {
        var startup=new StartupInfo{cb=Marshal.SizeOf<StartupInfo>()};
        if(!CreateProcess(executable,new StringBuilder("\""+executable+"\" "+arguments),IntPtr.Zero,IntPtr.Zero,false,4,IntPtr.Zero,Path.GetDirectoryName(executable)!,ref startup,out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"无法启动正规卸载器。");
        var process=new SafeProcessHandle(info.Process,true);using var thread=new SafeFileHandle(info.Thread,true);
        var job=CreateJobObject(IntPtr.Zero,null);
        try {
            if(job.IsInvalid||!AssignProcessToJobObject(job,process))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法完整跟踪卸载器，未运行卸载。");
            if(ResumeThread(thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法恢复卸载器。");
            return new(process,job);
        }catch{TerminateProcess(process,1);process.Dispose();job.Dispose();throw;}
    }
    public bool HasCompleted
    {
        get {
            var buffer=Marshal.AllocHGlobal(48);
            try {
                if(!QueryInformationJobObject(job,1,buffer,48,out _))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法确认卸载进程组是否结束，临时入口暂时保留。");
                return WaitForSingleObject(process,0)==0 && Marshal.ReadInt32(buffer,40)==0;
            }finally{Marshal.FreeHGlobal(buffer);}
        }
    }
    public async Task<int> WaitForCompletionAsync(CancellationToken token=default)
    {
        while(!HasCompleted)await Task.Delay(250,token).ConfigureAwait(false);
        if(!GetExitCodeProcess(process,out uint code))throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)code);
    }
    public void Dispose(){process.Dispose();job.Dispose();}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public int cb;public string? Reserved,Desktop,Title;
        public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
        public ushort Show,ReservedSize;public IntPtr Reserved2,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)]struct ProcessInfo{public IntPtr Process,Thread;public uint Id,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")][return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateJobObjectW")]static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool TerminateProcess(SafeProcessHandle process,uint code);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool QueryInformationJobObject(SafeFileHandle job,int information,IntPtr buffer,uint length,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
}
