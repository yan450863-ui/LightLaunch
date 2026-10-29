using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LightLaunch;

/// <summary>Owns only processes started during this manager session; never takes over namesakes.</summary>
public sealed class ProcessService : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer timer;
    private long lastRefresh;
    private bool disposed;

    public ProcessService() => timer = new(_ => { lock (gate) { if (!disposed) Refresh(); } }, null, 500, 500);

    public void Launch(SoftwareItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var entry = item.EntryPath ?? throw new InvalidOperationException("请先选择主程序。");
        if (!LibraryService.IsSafeEntryPath(item.FolderPath, entry))
            throw new InvalidOperationException("主程序不存在、超出软件目录，或路径包含目录链接，请重新选择。");
        var working = string.IsNullOrWhiteSpace(item.Preference.WorkingDirectoryRelative)
            ? Path.GetDirectoryName(entry)!
            : Path.GetFullPath(Path.Combine(item.FolderPath, item.Preference.WorkingDirectoryRelative));
        if (!Directory.Exists(working) || !SafeWorkingDirectory(item.FolderPath, working))
            throw new InvalidOperationException("工作目录不存在、超出软件目录，或包含目录链接。");
        var script = Path.GetExtension(entry).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(entry).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var executable = script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : entry;
        var arguments = item.Preference.Arguments ?? "";
        if (arguments.Contains('\0')) throw new InvalidOperationException("启动参数含无效字符。");
        var entryVariable = "LIGHTLAUNCH_ENTRY_" + Guid.NewGuid().ToString("N");
        // cmd /s strips the outermost pair of quotes; the inner pair protects spaces in the script path.
        var command = script
            ? $"\"{executable}\" /d /v:off /s /c \"\"%{entryVariable}%\"{(arguments.Length == 0 ? "" : " " + arguments)}\""
            : $"\"{executable}\"{(arguments.Length == 0 ? "" : " " + arguments)}";
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Refresh(force: true);
            if (sessions.TryGetValue(item.Id, out var old) && old.Alive.Count > 0)
                throw new InvalidOperationException("该软件已有本次管理器启动的进程正在运行。");
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var environment = IntPtr.Zero;
            ProcessInformation info;
            bool created;
            var createError = 0;
            try
            {
                if (script)
                {
                    // Expansion of a variable's value is not recursive: literal % and ! in entry paths stay literal.
                    var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
                        values[(string)pair.Key] = (string?)pair.Value ?? "";
                    values[entryVariable] = entry;
                    environment = Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
                }
                created = Native.CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                    0x00000004 | 0x00000400, environment, working, ref startup, out info);
                if (!created) createError = Marshal.GetLastWin32Error();
            }
            finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
            if (!created)
            {
                var error = createError;
                throw new Win32Exception(error, error == 740
                    ? "此程序需要管理员权限；首版不能可靠跟踪提权启动，请使用普通权限工具或单独启动。"
                    : "无法启动程序：" + new Win32Exception(error).Message);
            }
            using var process = new SafeProcessHandle(info.hProcess, true);
            using var thread = new SafeFileHandle(info.hThread, true);
            SafeFileHandle? job = null;
            try
            {
                var started = ReadIdentity(process, checked((int)info.dwProcessId))
                    ?? throw new InvalidOperationException("无法核实新进程的启动时间。");
                job = Native.CreateJobObject(IntPtr.Zero, null);
                if (job.IsInvalid || !Native.AssignProcessToJobObject(job, process))
                {
                    job.Dispose();
                    job = null;
                }
                var session = new Session { Job = job, Root = started };
                session.Known[started.Pid] = started;
                session.Alive[started.Pid] = started;
                old?.Job?.Dispose(); // No KILL_ON_JOB_CLOSE flag: releasing tracking never terminates software.
                sessions[item.Id] = session;
                job = null;
                if (Native.ResumeThread(thread) == uint.MaxValue)
                {
                    sessions.Remove(item.Id);
                    session.Job?.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复新进程。");
                }
                lastRefresh = 0;
            }
            catch
            {
                // Only a newly created, still-suspended process can reach this cleanup path.
                Native.TerminateProcess(process, 1);
                job?.Dispose();
                throw;
            }
        }
    }

    public TrackedProcessState GetState(string appId)
    {
        lock (gate)
        {
            if (disposed) return new(false, true, 0, "跟踪已关闭");
            Refresh();
            if (!sessions.TryGetValue(appId, out var session)) return new(false, false, 0, "未由本次管理器启动");
            var count = session.Alive.Count;
            var uncertain = session.Uncertain || session.Job is null;
            return new(count > 0, uncertain, count, count > 0
                ? $"运行中 · {count} 个进程{(uncertain ? "（部分状态无法确认）" : "")}" 
                : uncertain ? "状态未知：启动器已退出或跟踪受限" : "已停止（本次启动）");
        }
    }

    public async Task<StopResult> RequestStopAsync(string appId, CancellationToken cancellationToken = default)
    {
        Dictionary<int, Identity> targets;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Refresh(force: true);
            if (!sessions.TryGetValue(appId, out var session) || session.Alive.Count == 0)
                return new(true, false, "没有可安全停止的已跟踪进程。外部启动的进程需要自行退出。");
            targets = new(session.Alive);
        }
        var closeRequests = 0;
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var pid);
            if (targets.TryGetValue((int)pid, out var identity) && Native.IsWindowVisible(window)
                && Native.GetWindow(window, 4) == IntPtr.Zero && IsSameRunning(identity))
                if (Native.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) closeRequests++;
            return true;
        }, IntPtr.Zero);
        var until = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            var state = GetState(appId);
            if (!state.IsRunning) return new(true, false, state.IsUncertain ? "已跟踪进程已退出；仍可能存在无法确认的外部进程。" : "软件已正常退出。");
        }
        var final = GetState(appId);
        return new(!final.IsRunning, final.IsRunning, closeRequests > 0
            ? "已请求正常退出。请处理软件中的保存提示；仍在运行时可继续等待，或确认后强制结束。"
            : "没有可关闭的普通窗口。可在软件中自行退出，或确认后强制结束已跟踪进程。");
    }

    /// <summary>Caller must obtain explicit force-stop confirmation before calling this method.</summary>
    public void ForceStop(string appId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Refresh(force: true);
            if (!sessions.TryGetValue(appId, out var session)) return;
            var failures = new List<string>();
            // Never use Kill(entireProcessTree:true): its later name/PID lookup could affect a reused PID.
            foreach (var identity in session.Alive.Values.OrderByDescending(p => p.Started).ToArray())
            {
                using var handle = Native.OpenProcess(0x00100000 | 0x1000 | 0x0001, false, (uint)identity.Pid);
                if (handle.IsInvalid)
                {
                    if (IsSameRunning(identity)) failures.Add($"PID {identity.Pid}：没有结束权限");
                    continue;
                }
                var actual = ReadIdentity(handle, identity.Pid);
                if (actual != identity || Native.WaitForSingleObject(handle, 0) != 0x00000102) continue;
                if (!Native.TerminateProcess(handle, 1)) failures.Add($"PID {identity.Pid}：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
            lastRefresh = 0;
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private void Refresh(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - lastRefresh < 500) return;
        lastRefresh = now;
        if (sessions.Count == 0) return;
        Dictionary<int, int>? snapshot = null;
        foreach (var session in sessions.Values)
        {
            session.Uncertain = false;
            var alive = new Dictionary<int, Identity>();
            if (session.Job is not null && TryReadJobProcesses(session.Job, out var jobPids))
            {
                foreach (var pid in jobPids)
                {
                    using var handle = Native.OpenProcess(0x00100000 | 0x1000, false, (uint)pid);
                    if (handle.IsInvalid) { session.Uncertain = true; continue; }
                    var identity = ReadIdentity(handle, pid);
                    if (identity is null) { session.Uncertain = true; continue; }
                    // Job membership is checked on the same process handle to eliminate PID-reuse races.
                    if (!Native.IsProcessInJob(handle, session.Job, out var member) || !member) continue;
                    if (Native.WaitForSingleObject(handle, 0) == 0x00000102)
                        alive[pid] = identity;
                }
            }
            else
            {
                session.Uncertain = true;
                snapshot ??= ReadSnapshot();
                foreach (var known in session.Known.Values)
                    if (IsSameRunning(known)) alive[known.Pid] = known;
                // Conservative fallback: only attach a child while its verified parent is still alive.
                // Short-lived intermediates can be missed, so fallback never claims an exact stopped state.
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var candidate in snapshot)
                    {
                        if (alive.ContainsKey(candidate.Key) || !alive.TryGetValue(candidate.Value, out var parent)) continue;
                        using var handle = Native.OpenProcess(0x00100000 | 0x1000, false, (uint)candidate.Key);
                        if (handle.IsInvalid) continue;
                        var identity = ReadIdentity(handle, candidate.Key);
                        if (identity is null || identity.Started < parent.Started || !IsSameRunning(parent)) continue;
                        if (Native.WaitForSingleObject(handle, 0) != 0x00000102) continue;
                        alive[candidate.Key] = identity;
                        changed = true;
                    }
                }
            }
            foreach (var identity in alive.Values) session.Known[identity.Pid] = identity;
            session.Alive = alive;
        }
    }

    private static bool SafeWorkingDirectory(string folder, string working)
    {
        var current = Path.GetFullPath(working).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!current.Equals(parent, StringComparison.OrdinalIgnoreCase)
            && !current.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var directory = new DirectoryInfo(current); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static Identity? ReadIdentity(SafeProcessHandle handle, int pid)
        => Native.GetProcessTimes(handle, out var creation, out _, out _, out _) ? new(pid, creation) : null;

    private static bool IsSameRunning(Identity identity)
    {
        using var handle = Native.OpenProcess(0x00100000 | 0x1000, false, (uint)identity.Pid);
        return !handle.IsInvalid && ReadIdentity(handle, identity.Pid) == identity
            && Native.WaitForSingleObject(handle, 0) == 0x00000102;
    }

    private static bool TryReadJobProcesses(SafeFileHandle job, out List<int> pids)
    {
        pids = new();
        var capacity = 32;
        while (capacity <= 65536)
        {
            var size = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.QueryInformationJobObject(job, 3, buffer, (uint)size, out _))
                {
                    if (Marshal.GetLastWin32Error() == 234) { capacity *= 2; continue; }
                    return false;
                }
                var assigned = Marshal.ReadInt32(buffer);
                var count = Marshal.ReadInt32(buffer, 4);
                if (assigned > count) { capacity = Math.Max(capacity * 2, assigned); continue; }
                for (var i = 0; i < count; i++) pids.Add(checked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64()));
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    private static Dictionary<int, int> ReadSnapshot()
    {
        var result = new Dictionary<int, int>();
        using var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return result;
        var entry = new ProcessEntry { dwSize = (uint)Marshal.SizeOf<ProcessEntry>(), szExeFile = "" };
        if (!Native.Process32First(snapshot, ref entry)) return result;
        do { result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
        while (Native.Process32Next(snapshot, ref entry));
        return result;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            foreach (var session in sessions.Values) session.Job?.Dispose();
            sessions.Clear();
        }
    }

    private sealed record Identity(int Pid, long Started);
    private sealed class Session
    {
        public required Identity Root;
        public SafeFileHandle? Job;
        public Dictionary<int, Identity> Known = new();
        public Dictionary<int, Identity> Alive = new();
        public bool Uncertain;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint size, out uint returnedSize);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr argument);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr argument);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
