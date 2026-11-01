using System.Collections;
using System.Reflection;
using System.Text.Json;
using LightLaunch;

internal static class Checks
{
    [STAThread]
    static void Main(string[] args)
    {
        string temp = Path.Combine(AppContext.BaseDirectory, "location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string oldHome = Path.Combine(temp, "old"), newHome = Path.Combine(temp, "new"), oldRoot = Path.Combine(oldHome, "软件文件夹");
            string oldApp = Path.Combine(oldRoot, "Tool"), oldExe = Path.Combine(oldApp, "Tool.exe");
            Directory.CreateDirectory(oldApp); File.WriteAllText(oldExe, "sample");
            var settings = new LauncherSettings { LibraryRoot = oldRoot, Layout = LibraryLayout.Direct };
            var service = new LibraryService(settings); var first = service.Scan(oldRoot, CancellationToken.None);
            Assert(first.Items.Count == 1, "discover initial item");
            first.Items[0].Preference.DisplayName = "别名";
            var assembly = typeof(LibraryService).Assembly;
            var relocate = assembly.GetType("LightLaunch.LibraryRelocation")!.GetMethod("Recover")!;
            relocate.Invoke(null, new object[] { settings, oldHome });
            Assert(settings.LibraryRootIdentity is not null, "record root identity");
            var legacy = new LauncherSettings { LibraryRoot = oldRoot, Layout = LibraryLayout.Direct,
                FolderIdentities = new Dictionary<string, string>(settings.FolderIdentities, StringComparer.OrdinalIgnoreCase) };
            Directory.Move(oldHome, newHome);
            relocate.Invoke(null, new object[] { legacy, newHome });
            Assert(legacy.LibraryRoot == Path.Combine(newHome, "软件文件夹"), "recover legacy root using software identities");
            relocate.Invoke(null, new object[] { settings, newHome });
            string newRoot = Path.Combine(newHome, "软件文件夹");
            Assert(settings.LibraryRoot == newRoot, "recover renamed launcher root");
            var relocated = service.Scan(newRoot, CancellationToken.None);
            Assert(relocated.Items.Single().DisplayName == "别名", "retain alias by identity");

            string migrationData = Path.Combine(temp, "migration-data"), records = Path.Combine(migrationData, "迁入记录"); Directory.CreateDirectory(records);
            var registration = new UninstallRegistration("HKCU|64|sample", "sample", Path.Combine(temp, "original"), "uninstall.exe", "sample");
            var record = new MigrationRecord(Guid.NewGuid(), oldRoot, relocated.Items[0].Id, oldApp, Path.Combine(temp, "original"), "Tool.exe", registration, null, "Detached", DateTimeOffset.Now);
            File.WriteAllText(Path.Combine(records, record.Id.ToString("N") + ".json"), JsonSerializer.Serialize(record));
            var migrations = new MigrationService(migrationData, () => new UninstallRegistrationList(new[] { registration }, true));
            Directory.CreateDirectory(record.OriginalPath);
            bool occupiedRejected = false;
            try { migrations.RecoverInterrupted(newRoot, relocated.Items[0]); } catch (IOException) { occupiedRejected = true; }
            Directory.Delete(record.OriginalPath);
            Assert(occupiedRejected, "reject occupied original migration location");
            var incomplete = new MigrationService(migrationData, () => new UninstallRegistrationList(new[] { registration }, false));
            bool incompleteRejected = false;
            try { incomplete.RecoverInterrupted(newRoot, relocated.Items[0]); } catch (IOException) { incompleteRejected = true; }
            Assert(incompleteRejected, "reject incomplete uninstall registry evidence");
            var rebound = migrations.RecoverInterrupted(newRoot, relocated.Items[0]);
            Assert(rebound?.TargetPath == Path.Combine(newRoot, "Tool"), "rebind detached migration record by identity and registration");

            string desktop = Path.Combine(temp, "desktop"), data = Path.Combine(temp, "data"); Directory.CreateDirectory(desktop);
            string lnk = Path.Combine(desktop, "Tool.lnk"); dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(lnk); link.TargetPath = oldExe; link.Arguments = "--sample"; link.WorkingDirectory = oldApp; link.IconLocation = oldExe + ",0"; link.Save();
            Type dialogType = assembly.GetType("LightLaunch.ShortcutRepairDialog")!;
            using var dialog = (Form)Activator.CreateInstance(dialogType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object?[] { relocated.Items.ToArray(), settings, new MigrationService(data), data, new[] { desktop } }, null)!;
            dialogType.GetMethod("Scan", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, null);
            var candidates = (IList)dialogType.GetField("candidates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            Assert(candidates.Count == 1, "identify matching broken shortcut");
            dialogType.GetMethod("RepairOne", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, new object[] { candidates[0]!, shell });
            link = shell.CreateShortcut(lnk);
            string newExe = Path.Combine(newRoot, "Tool", "Tool.exe");
            Assert(string.Equals((string)link.TargetPath, newExe, StringComparison.OrdinalIgnoreCase), "repair target");
            Assert((string)link.Arguments == "--sample", "preserve arguments");
            Assert(string.Equals((string)link.WorkingDirectory, Path.GetDirectoryName(newExe), StringComparison.OrdinalIgnoreCase), "repair working directory");
            Assert(((string)link.IconLocation).StartsWith(newExe, StringComparison.OrdinalIgnoreCase), "repair icon");
            Assert(Directory.GetFiles(Path.Combine(data, "快捷方式备份"), "*.lnk", SearchOption.AllDirectories).Length == 1, "backup shortcut");
            bool rejected = false;
            try { dialogType.GetMethod("RepairOne", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, new object[] { candidates[0]!, shell }); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
            Assert(rejected, "reject shortcut changed since detection");
            string renamed = Path.Combine(newRoot, "Renamed"); Directory.Move(Path.Combine(newRoot, "Tool"), renamed);
            service.Scan(newRoot, CancellationToken.None);
            Assert(settings.PreviousLocations.ContainsKey(Path.Combine(newRoot, "Tool")), "retain prior app location for shortcut repair");
            Console.WriteLine("PASS all location and shortcut checks");
        }
        finally { Directory.Delete(temp, true); }
        if (args.Length == 1)
        {
            string data = Path.Combine(args[0], "数据");
            var store = new SettingsStore(data);
            var scan = new LibraryService(store.Settings).Scan(store.Settings.LibraryRoot!, CancellationToken.None);
            Type dialogType = typeof(LibraryService).Assembly.GetType("LightLaunch.ShortcutRepairDialog")!;
            using var dialog = (Form)Activator.CreateInstance(dialogType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object?[] { scan.Items.ToArray(), store.Settings, new MigrationService(data), data, null }, null)!;
            dialogType.GetMethod("Scan", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, null);
            var candidates = (IList)dialogType.GetField("candidates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            Console.WriteLine($"Read-only real desktop check: {candidates.Count} matching candidates; none modified.");
        }
    }
    static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
