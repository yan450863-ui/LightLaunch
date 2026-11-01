using LightLaunch;
using Microsoft.Win32;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static bool Refused(Action action) { try { action(); return false; } catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return true; } }
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        string scratch = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(scratch, "软件库"), folder = Path.Combine(root, "工具"), old = Path.Combine(scratch, "旧位置", "工具");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(Path.Combine(folder, "main.exe"), "fixture, never executed");
        var settings = new LauncherSettings { LibraryRoot = root, Layout = LibraryLayout.Direct };
        var item = new LibraryService(settings).Scan(root, default).Items.Single();
        Check(LibraryService.LibraryRootProblem(Path.GetPathRoot(root)!) is not null, "Volume root rejected");
        Check(LibraryService.LibraryRootProblem(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is not null, "Profile root rejected");
        Check(LibraryService.LibraryRootProblem(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")) is not null, "Windows subtree rejected");
        Check(LibraryService.LibraryRootProblem(root) is null, "Dedicated library accepted");
        Check(!new LibraryService(settings).Scan(Path.GetPathRoot(root)!, default).RootAvailable, "Unsafe saved root is not traversed");
        bool Event(string relative, WatcherChangeTypes change, LibraryLayout layout = LibraryLayout.Direct)
            => LibraryService.ShouldRescanChange(root, layout, Path.Combine(root, relative), change);
        Check(!Event(@"工具\log.txt", WatcherChangeTypes.Changed), "Log write ignored");
        Check(!Event(@"工具\data\settings.json", WatcherChangeTypes.Created), "Nested configuration creation ignored");
        Check(!Event("工具", WatcherChangeTypes.Changed), "Directory timestamp ignored");
        Check(Event("新软件", WatcherChangeTypes.Created), "Software folder arrival detected");
        Check(Event("工具", WatcherChangeTypes.Deleted), "Software removal detected");
        Check(Event(@"工具\bin\new.exe", WatcherChangeTypes.Created), "Nested executable arrival detected");
        Check(Event(@"工具\bin\start.cmd", WatcherChangeTypes.Changed), "Entry overwrite detected");
        Check(Event(@"分类\工具", WatcherChangeTypes.Renamed, LibraryLayout.Categorized), "Categorized software rename detected");
        Check(!Event(@"分类\工具\logs", WatcherChangeTypes.Created, LibraryLayout.Categorized), "Categorized log directory ignored");
        var data = Path.Combine(scratch, "数据"); var store = new SettingsStore(data); store.Settings.LibraryRoot = root; store.Save();
        string config = Path.Combine(data, "settings.json");
        File.SetLastWriteTimeUtc(config, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(config); store.Save();
        Check(File.GetLastWriteTimeUtc(config) == timestamp, "Unchanged settings are not rewritten");
        store.Settings.LibraryRoot = root + "-changed"; store.Save();
        Check(new SettingsStore(data).Settings.LibraryRoot == root + "-changed", "Changed settings still persist");
        var dialogType = typeof(LibraryService).Assembly.GetType("LightLaunch.EntryDialog", true)!;
        using (var dialog = (Form)Activator.CreateInstance(dialogType, item)!) {
            ((TextBox)dialogType.GetField("working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!).Text = @".\";
            dialogType.GetMethod("SaveEntry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
            Check(dialog.DialogResult == DialogResult.OK, "Entry dialog accepts dot-backslash working directory");
        }
        var registration = new UninstallRegistration("CurrentUser|Registry64|LightLaunch.ReviewFixture", "工具", old, "\"" + Path.Combine(old, "uninstall.exe") + "\"", "Fixture");
        var legacy = registration with { Id = registration.Id.Replace("Registry64", "Registry32") };
        item.Preference.UninstallRegistryId = legacy.Id;
        Check(UninstallService.ProposeDefault(item, new[] { registration }).Registration == registration, "Saved HKCU32 choice resolves to shared HKCU64 record");
        Check(!UninstallService.SameRegistrationId("LocalMachine|Registry32|Fixture", "LocalMachine|Registry64|Fixture"), "HKLM views stay distinct");
        Check(!UninstallService.SameRegistration(registration, legacy with { Command = "changed.exe" }), "Changed uninstall command is rejected despite shared identity");
        Check(UninstallService.SoftwareViews(RegistryHive.CurrentUser, @"Software\Classes\CLSID").Length == 2, "Redirected HKCU Classes subtree keeps both views");
        CheckUiRefresh(scratch);
        bool collide = false;
        var service = new MigrationService(data, () => {
            if (collide) { collide = false; Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "保留.txt"), "foreign"); }
            return new UninstallRegistrationList(new[] { registration }, true);
        });
        var plan = service.Prepare(root, item, legacy, old);
        var record = service.Commit(plan);
        Check(record.State == "Detached" && plan.Layout == LibraryLayout.Direct, "Direct-layout migration commits without permanent junction");
        service.ValidateForUninstall(root, item, registration);
        Check(true, "Legacy migration record accepts unchanged canonical registration");
        collide = true;
        Check(Refused(() => service.BeginUninstallCompatibility(root, item, registration)), "Foreign directory appearing after validation blocks junction creation");
        Check(service.GetForItem(item)?.State == "Detached", "Failed creation rolls back temporary state");
        Check(File.ReadAllText(Path.Combine(old, "保留.txt")) == "foreign", "Foreign directory contents preserved");
        string recordPath = Path.Combine(data, "迁入记录", record.Id.ToString("N") + ".json");
        File.WriteAllText(recordPath, JsonSerializer.Serialize(record with { State = "TemporaryPrepared" }));
        Check(service.RecoverInterrupted(root, item)?.State == "Detached", "Interrupted prepared state with ordinary directory recovers");
        Check(Refused(() => service.ValidateForUninstall(root, item, registration)), "Occupied old path still blocks uninstall");
        Directory.Move(old, Path.Combine(scratch, "保留的外来目录"));
        try { Check(service.BeginUninstallCompatibility(root, item, registration), "Retry creates temporary junction once old path is clear"); }
        catch(ArgumentException) when (new DirectoryInfo(old).LinkTarget?.StartsWith(@"Global\", StringComparison.Ordinal) == true) {
            Check(service.GetForItem(item)?.State == "TemporaryPrepared", "Unverifiable junction preserves durable recovery intent");
            Console.WriteLine("SKIP successful junction lifecycle: execution environment rewrites the NT target to Global; existing local-path policy rejects it. Fixture retained for inspection.");
            Console.WriteLine($"Review checks: {passed} passed; 3 native junction lifecycle checks blocked by environment. No installed software modified.");
            return;
        }
        Check(DirectoryJunction.Read(old)?.Target == folder, "Temporary junction targets exact managed folder");
        service.RemoveCompatibility(root, item, true);
        Check(DirectoryJunction.Read(old) is null && File.ReadAllText(Path.Combine(folder, "main.exe")) == "fixture, never executed", "Removing temporary junction preserves managed software");
        Console.WriteLine($"Review checks: {passed} passed. Only isolated fixtures changed; no installed software modified.");
    }

    static void CheckUiRefresh(string scratch)
    {
        string root = Path.Combine(scratch, "界面软件库"), data = Path.Combine(scratch, "界面数据");
        for (int i = 0; i < 45; i++) {
            string folder = Path.Combine(root, "Tool" + i.ToString("D2")); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.exe"), "fixture");
        }
        var store = new SettingsStore(data); store.Settings.LibraryRoot = root; store.Settings.Layout = LibraryLayout.Direct; store.Save();
        var type = typeof(LibraryService).Assembly.GetType("LightLaunch.MainForm", true)!;
        using var form = (Form)Activator.CreateInstance(type, data)!;
        using var host = new Form { ClientSize = form.ClientSize, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 100, 100) };
        form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None; form.MinimumSize = Size.Empty; form.Dock = DockStyle.Fill;
        host.Controls.Add(form); form.Show();
        using var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        Exception? failure = null;
        object Field(string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        timer.Tick += async (_, _) => {
            timer.Stop();
            try {
                var grid = (DataGridView)Field("grid"); Check(grid.Rows.Count == 45, "Fixture library loads in real main form");
                grid.FirstDisplayedScrollingRowIndex = 20;
                string? topId = (grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag as SoftwareItem)?.Id;
                type.GetMethod("RenderRows", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                Check((grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag as SoftwareItem)?.Id == topId, "List rebuild retains first visible software");
                var scanTime = (DateTime)Field("lastPeriodicScan");
                File.WriteAllText(Path.Combine(root, "Tool00", "run.log"), "log write");
                await Task.Delay(1600);
                Check((DateTime)Field("lastPeriodicScan") == scanTime, "Real log write does not trigger full rescan");
                string added = Path.Combine(root, "Added"); Directory.CreateDirectory(added); File.WriteAllText(Path.Combine(added, "main.exe"), "fixture");
                await Task.Delay(2500);
                Check(grid.Rows.Count == 46, "Real folder arrival still refreshes software list");
            } catch(Exception error) { failure = error; }
            finally { host.Close(); }
        };
        host.Shown += (_, _) => timer.Start(); Application.Run(host);
        if (failure is not null) throw failure;
    }
}
