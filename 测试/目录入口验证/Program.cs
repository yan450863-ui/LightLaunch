using LightLaunch;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        string root = Path.Combine(fixtures, "软件文件夹"); Directory.CreateDirectory(root);
        void Put(string relative) { string file = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "fixture"); }
        Put("YunX-Desktop/YunX-Desktop.exe"); Put("YunX-Desktop/unins000.exe"); Put("YunX-Desktop/app/application.jar");
        Put("YunX-Desktop/data/settings.json"); Put("YunX-Desktop/runtime/bin/java.exe");
        Put("Editor/first.exe"); Put("Editor/second.exe"); Put("Single.exe"); Put("Other.exe"); Put("shared.txt");
        var settings = new LauncherSettings { LibraryRoot = root, Layout = LibraryLayout.Direct };
        var service = new LibraryService(settings);
        var scan = service.Scan(root, default);
        Check(scan.Items.Count == 4, "Two complete folders and two single EXEs produce exactly four items");
        var yunx = scan.Items.Single(x => x.Name == "YunX-Desktop");
        Check(yunx.EntryPath == Path.GetFullPath(Path.Combine(root, "YunX-Desktop/YunX-Desktop.exe")), "Top-level main is selected over uninstaller and Java runtime");
        Check(!scan.Items.Any(x => x.Name is "app" or "data" or "runtime"), "Internal folders never become software items");
        Check(scan.Items.Single(x => x.Name == "Editor").EntryPath is null, "Two actual main programs require a choice");
        Check(LibraryService.IsCurrentSoftwareItem(root, yunx), "Direct folder identity validation succeeds");
        Check(!LibraryService.IsSafeSoftwareFolder(root, root, LibraryLayout.Direct), "Library root cannot be deleted as software");
        Check(!LibraryService.IsSafeSoftwareFolder(root, Path.Combine(yunx.FolderPath, "data"), LibraryLayout.Direct), "Direct layout rejects internal folder scope");
        Check(!LibraryService.IsSafeEntryPath(yunx.FolderPath, Path.Combine(root, "Single.exe")), "Manual entry cannot escape its software folder");
        yunx.SetDisplayName("云端桌面");
        Check(yunx.Name == "YunX-Desktop" && yunx.DisplayName == "云端桌面" && Directory.Exists(yunx.FolderPath), "Chinese alias preserves original name, folder and identity");
        var data = Path.Combine(fixtures, "数据"); var store = new SettingsStore(data);
        store.Settings.LibraryRoot = root; store.Settings.Layout = settings.Layout;
        store.Settings.Apps = settings.Apps; store.Settings.FolderIdentities = settings.FolderIdentities; store.Save();
        var loaded = new SettingsStore(data);
        Check(new LibraryService(loaded.Settings).Scan(root, default).Items.Single(x => x.Id == yunx.Id).DisplayName == "云端桌面", "Alias and directory mode survive reload");
        var single = scan.Items.Single(x => x.Name == "Single");
        Check(single.IsSingleFile && single.EntryPath == Path.Combine(root, "Single.exe"), "Single EXE has an exact immutable executable target");
        Check(service.CalculateItemSize(single, default).Bytes == new FileInfo(single.EntryPath!).Length, "Single EXE size excludes siblings");
        Check(LibraryService.IsCurrentSoftwareItem(root, single), "Single EXE file identity is validated");
        File.Move(single.SingleFilePath!, Path.Combine(root, "Renamed.exe"));
        Check(!LibraryService.IsCurrentSoftwareItem(root, single), "Moved single EXE rejects a stale deletion target");
        single.SetDisplayName("中文独立工具");
        var renamed = service.Scan(root, default).Items.Single(x => x.Id == single.Id);
        Check(renamed.DisplayName == "中文独立工具" && renamed.Name == "Renamed", "Single EXE alias survives a same-volume rename");
        File.Move(renamed.SingleFilePath!, Path.Combine(root, "Original.saved")); Put("Renamed.exe");
        Check(!LibraryService.IsCurrentSoftwareItem(root, renamed), "Replacement EXE at the same path cannot inherit deletion approval");
        bool rejected = false;
        try { new UninstallService().RecycleSingleFile(root, renamed); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.Exists(Path.Combine(root, "Other.exe")) && File.Exists(Path.Combine(root, "shared.txt")), "Stale recycle request preserves replacement and all sibling files");
        if (args.Contains("--recycle-fixture"))
        {
            var current = service.Scan(root, default).Items.Single(x => x.Name == "Renamed");
            new UninstallService().RecycleSingleFile(root, current);
            Check(!File.Exists(current.SingleFilePath) && Directory.Exists(root) && File.Exists(Path.Combine(root, "Other.exe")) && File.Exists(Path.Combine(root, "shared.txt")), "Native recycle removes only the selected fixture EXE and preserves siblings");
            Put("Renamed.exe");
        }
        var oldData = Path.Combine(fixtures, "旧配置"); Directory.CreateDirectory(oldData);
        File.WriteAllText(Path.Combine(oldData, "settings.json"), JsonSerializer.Serialize(new { LibraryRoot = root }));
        Check(new SettingsStore(oldData).Settings.Layout == LibraryLayout.Categorized, "Existing settings without a layout keep categorized semantics");
        string categoriesRoot = Path.Combine(fixtures, "分类库"); Directory.CreateDirectory(Path.Combine(categoriesRoot, "工具/示例"));
        File.WriteAllText(Path.Combine(categoriesRoot, "工具/示例/main.cmd"), "@exit /b");
        Check(new LibraryService(new LauncherSettings()).Scan(categoriesRoot, default).Items.Single().Name == "示例", "Categorized library remains compatible");
        using (var icon = SoftwareIconService.Load(yunx)) Check(icon.Width > 0 && icon.Height > 0, "Invalid executable icon falls back without crashing");
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            var actualSettings = new LauncherSettings { Layout = LibraryLayout.Direct };
            var actual = new LibraryService(actualSettings).Scan(Path.GetFullPath(args[0]), default);
            Check(actual.RootAvailable && actual.Items.Count >= 4, "Actual user library is readable without executing software");
            foreach (var item in actual.Items)
            {
                Console.WriteLine($"READONLY {item.Name} => {item.Preference.EntryRelativePath}");
                Check(item.EntryPath is not null, "Actual top-level launcher detected: " + item.Name);
                using var icon = SoftwareIconService.Load(item);
                using var fallback = SystemIcons.Application.ToBitmap();
                using var actualImage = new MemoryStream(); using var fallbackImage = new MemoryStream();
                icon.Save(actualImage, System.Drawing.Imaging.ImageFormat.Png); fallback.Save(fallbackImage, System.Drawing.Imaging.ImageFormat.Png);
                Check(!actualImage.ToArray().SequenceEqual(fallbackImage.ToArray()), "Actual program has its own icon: " + item.Name);
            }
        }
        // Render only our own fresh fixture data in an off-screen non-activating host.
        using var form = (Form)Activator.CreateInstance(typeof(SettingsStore).Assembly.GetType("LightLaunch.MainForm", true)!, data)!;
        using var host = new Host { ClientSize = form.ClientSize, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 100, 100) };
        form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None; form.MinimumSize = Size.Empty; form.Dock = DockStyle.Fill; host.Controls.Add(form); form.Show();
        using var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) => {
            timer.Stop();
            var grid = (DataGridView)form.GetType().GetField("grid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            Check(grid.Rows.Count == 4, "Main window shows complete software and standalone EXEs");
            string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../..", "记录")); Directory.CreateDirectory(output);
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "新版主界面.png"));
            var rename = (Button)form.GetType().GetField("rename", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            var open = (Button)form.GetType().GetField("open", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            Check(form.PointToClient(rename.PointToScreen(Point.Empty)).X > grid.Right && rename.Parent!.ClientRectangle.Contains(rename.Bounds), "Rename action is in the right-hand function panel");
            Check(open.Parent!.ClientRectangle.Contains(open.Bounds), "Action buttons fit their cells at current DPI");
            var picture = (PictureBox)form.GetType().GetField("selectedIcon", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            Check(picture.Width >= 64, "Selected program icon remains large enough in the function panel");
            Check(grid.Columns["OriginalName"]!.DisplayIndex == 2 && grid.Rows.Cast<DataGridViewRow>().Any(row => (string?)row.Cells["AppName"].Value == "云端桌面" && (string?)row.Cells["OriginalName"].Value == "YunX-Desktop"), "Original names appear in a separate column after aliases");
            ((Label)form.GetType().GetField("rootLabel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!).Text = "软件文件夹";
            ((Label)form.GetType().GetField("selectedPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!).Text = "位置：软件文件夹 / YunX-Desktop";
            using var clean = new Bitmap(form.Width, form.Height); form.DrawToBitmap(clean, new Rectangle(Point.Empty, clean.Size)); clean.Save(Path.Combine(output, "公开示意.png"));
            host.Close();
        };
        host.Shown += (_, _) => timer.Start(); Application.Run(host);
        Console.WriteLine($"Directory checks: {passed} passed. Actual user programs were never executed or modified.");
    }
    sealed class Host : Form { protected override bool ShowWithoutActivation => true; }
}
