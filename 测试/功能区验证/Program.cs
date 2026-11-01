using System.Reflection;
using System.Runtime.InteropServices;
using LightLaunch;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window, int itemId);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxLength);
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message); checks++;
    }
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static bool SameLines(string left, string right) => left.Replace("\r\n", "\n") == right.Replace("\r\n", "\n");
    static async Task WaitUntil(Func<bool> predicate, string message, int timeout = 12000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(100);
        Check(predicate(), message);
    }
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--fixture")) {
            using var fixture = new Form { Text = "轻启隔离进程测试", ClientSize = new Size(200, 100), ShowInTaskbar = true,
                StartPosition = FormStartPosition.Manual, Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 300, 100) };
            bool allowClose = false;
            if (args.Contains("--block")) fixture.FormClosing += (_, eventArgs) => { if (!allowClose) eventArgs.Cancel = true; };
            using var exitTimer = new System.Windows.Forms.Timer { Interval = 100 };
            fixture.Shown += (_, _) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "窗口就绪.txt"), "fixture ready");
            exitTimer.Tick += (_, _) => { if (File.Exists(Path.Combine(AppContext.BaseDirectory, "允许退出.txt"))) { allowClose = true; fixture.Close(); } };
            exitTimer.Start();
            Application.Run(fixture); return;
        }

        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixtures, "软件库"), folder = Path.Combine(root, "Example"), data = Path.Combine(fixtures, "数据");
        Directory.CreateDirectory(folder);
        string source1 = Path.Combine(fixtures, "原图一.png"), source2 = Path.Combine(fixtures, "原图二.jpg"), invalid = Path.Combine(fixtures, "无效图片.png");
        using (var firstImage = new Bitmap(1200, 600)) { using var canvas = Graphics.FromImage(firstImage); canvas.Clear(Color.DodgerBlue); firstImage.Save(source1, System.Drawing.Imaging.ImageFormat.Png); }
        using (var secondImage = new Bitmap(120, 240)) { using var canvas = Graphics.FromImage(secondImage); canvas.Clear(Color.OrangeRed); secondImage.Save(source2, System.Drawing.Imaging.ImageFormat.Jpeg); }
        File.WriteAllText(invalid, "not an image");
        foreach (string name in new[] { "FeatureChecks.exe", "FeatureChecks.dll", "FeatureChecks.deps.json", "FeatureChecks.runtimeconfig.json", "LightLaunch.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(folder, name));
        string other = Path.Combine(root, "Other"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "other.exe"), "fixture");
        var settings = new LauncherSettings { LibraryRoot = root, Layout = LibraryLayout.Direct };
        var scan = new LibraryService(settings).Scan(root, default);
        var app = scan.Items.Single(item => item.Name == "Example"); app.Preference.Arguments = "--fixture";
        var store = new SettingsStore(data); store.Settings.LibraryRoot = root; store.Settings.Layout = LibraryLayout.Direct;
        store.Settings.Apps = settings.Apps; store.Settings.FolderIdentities = settings.FolderIdentities; store.Save();

        using var form = (Form)Activator.CreateInstance(typeof(SettingsStore).Assembly.GetType("LightLaunch.MainForm", true)!, data)!;
        using var host = new Host { ClientSize = form.ClientSize, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 100, 100) };
        form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None; form.MinimumSize = Size.Empty; form.Dock = DockStyle.Fill; host.Controls.Add(form); form.Show();
        using var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        Exception? failure = null;
        timer.Tick += async (_, _) => {
            timer.Stop();
            try {
                var grid = (DataGridView)Field(form, "grid");
                Check(grid.Rows.Count == 2, "fixture library loads two software items");
                var sort = (ComboBox)Field(form, "sortMode");
                Check(sort.SelectedIndex == 0 && new SettingsStore(data).Settings.Sort == LibrarySort.Recent, "recent use is the default sort for existing settings");
                var first = grid.Rows.Cast<DataGridViewRow>().Single(row => (row.Tag as SoftwareItem)?.Id == app.Id);
                grid.CurrentCell = first.Cells[1];
                var description = (RichTextBox)Field(form, "selectedDescription");
                var toggle = (Button)Field(form, "launch"); var rename = (Button)Field(form, "rename");
                Check(description.Text == "暂无简介" && toggle.Text == "启动", "old configuration has empty description and one start button");

                void Edit(bool save, string alias, string notes, Action<Form>? configure = null) {
                    using var modalTimer = new System.Windows.Forms.Timer { Interval = 100 };
                    Exception? modalFailure = null;
                    modalTimer.Tick += (_, _) => {
                        var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window.GetType().Name == "RenameDialog");
                        if (dialog is null) return;
                        modalTimer.Stop();
                        try {
                        var nameField = (TextBox)Field(dialog, "name");
                        var descriptionField = (TextBox)Field(dialog, "description");
                        nameField.Text = alias; descriptionField.Text = notes;
                        configure?.Invoke(dialog);
                        if (save && notes.Length > 0) {
                            var nameBounds = dialog.RectangleToClient(nameField.RectangleToScreen(nameField.ClientRectangle));
                            var descriptionBounds = dialog.RectangleToClient(descriptionField.RectangleToScreen(descriptionField.ClientRectangle));
                            var saveButton = (Button)dialog.AcceptButton!;
                            var buttonBounds = dialog.RectangleToClient(saveButton.RectangleToScreen(saveButton.ClientRectangle));
                            Check(nameBounds.Bottom <= descriptionBounds.Top && descriptionBounds.Bottom <= buttonBounds.Top && dialog.ClientRectangle.Contains(buttonBounds), "dialog fields and save button fit without overlapping");
                            string output = Path.Combine(AppContext.BaseDirectory, "../../..", "记录"); Directory.CreateDirectory(output);
                            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "别名与简介.png"));
                        }
                        if (save) ((Button)dialog.AcceptButton!).PerformClick(); else ((Button)dialog.CancelButton!).PerformClick();
                        } catch (Exception error) { modalFailure = error; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                    };
                    modalTimer.Start(); rename.PerformClick();
                    if (modalFailure is not null) throw modalFailure;
                }
                void SelectImage(Form dialog, int slot, string path)
                    => dialog.GetType().GetMethod("SetPictureFromFile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { slot, path });
                void RemoveImage(Form dialog, int slot)
                    => dialog.GetType().GetMethod("RemovePicture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { slot });
                const string notes = "用于整理工具\r\n第二行说明";
                Edit(false, "取消的别名", "取消的简介", dialog => SelectImage(dialog, 0, source1));
                Check(!Directory.Exists(Path.Combine(data, "软件图片")), "cancelled image choice does not create stored files");
                Edit(true, "中文别名", notes, dialog => { SelectImage(dialog, 0, source1); SelectImage(dialog, 1, source2); });
                Check(SameLines(description.Text, notes) && (string?)grid.CurrentRow!.Cells["AppName"].Value == "中文别名", "dialog saves alias and multiline description to the main panel");
                var loaded = new SettingsStore(data).Settings.Apps[app.Id];
                Check(loaded.DisplayName == "中文别名" && loaded.Description == notes, "alias and description survive reloading settings");
                Check(loaded.Picture1 is not null && loaded.Picture2 is not null && loaded.Picture1 != loaded.Picture2, "both image slots persist separate references");
                string saved1 = Path.Combine(data, "软件图片", loaded.Picture1!), saved2 = Path.Combine(data, "软件图片", loaded.Picture2!);
                Check(File.ReadAllBytes(saved1).SequenceEqual(File.ReadAllBytes(source1)) && File.ReadAllBytes(saved2).SequenceEqual(File.ReadAllBytes(source2)), "both original images are copied byte-for-byte into the data directory");
                var boxes = (PictureBox[])Field(form, "selectedPictures");
                Check(boxes.Length == 2 && boxes.All(box => box.Image is not null), "both thumbnails appear in the selected software panel");
                Check(boxes[0].Image!.Width == 600 && boxes[0].Image!.Height == 300, "large image is scaled for the thumbnail without changing its saved copy");
                var iconBounds = form.RectangleToClient(((PictureBox)Field(form, "selectedIcon")).RectangleToScreen(((PictureBox)Field(form, "selectedIcon")).ClientRectangle));
                var nameBounds = form.RectangleToClient(((Label)Field(form, "selectedName")).RectangleToScreen(((Label)Field(form, "selectedName")).ClientRectangle));
                var statusBounds = form.RectangleToClient(((Label)Field(form, "selectedStatus")).RectangleToScreen(((Label)Field(form, "selectedStatus")).ClientRectangle));
                var sizeBounds = form.RectangleToClient(((Label)Field(form, "selectedSize")).RectangleToScreen(((Label)Field(form, "selectedSize")).ClientRectangle));
                Check(nameBounds.Left >= iconBounds.Right && statusBounds.Left >= iconBounds.Right && sizeBounds.Left >= iconBounds.Right, "name, status and size are to the right of the icon");
                var leftBounds = form.RectangleToClient(boxes[0].RectangleToScreen(boxes[0].ClientRectangle));
                var rightBounds = form.RectangleToClient(boxes[1].RectangleToScreen(boxes[1].ClientRectangle));
                Check(leftBounds.Right <= rightBounds.Left && leftBounds.Top == rightBounds.Top, "two thumbnails are side by side without overlapping");
                bool enlarged = false;
                using (var viewerTimer = new System.Windows.Forms.Timer { Interval = 100 }) {
                    viewerTimer.Tick += (_, _) => {
                        var viewer = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window.Text == "软件图片");
                        if (viewer is null) return;
                        viewerTimer.Stop();
                        enlarged = viewer.Controls.OfType<PictureBox>().Any(box => box.Image is not null && box.Image.Width == 1200 && box.Image.Height == 600);
                        viewer.Close();
                    };
                    viewerTimer.Start();
                    typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boxes[0], new object[] { EventArgs.Empty });
                }
                Check(enlarged, "clicking a thumbnail opens the full image viewer");
                File.Delete(source1); File.Delete(source2);
                Check(Directory.Exists(folder) && File.Exists(Path.Combine(folder, "FeatureChecks.exe")), "renaming display leaves actual folder and executable unchanged");
                Edit(false, "取消的名称", "取消的简介");
                loaded = new SettingsStore(data).Settings.Apps[app.Id];
                Check(loaded.DisplayName == "中文别名" && loaded.Description == notes && SameLines(description.Text, notes), "cancel preserves both saved fields");
                var second = grid.Rows.Cast<DataGridViewRow>().Single(row => (row.Tag as SoftwareItem)?.Name == "Other");
                grid.CurrentCell = second.Cells[1];
                Check(description.Text == "暂无简介" && boxes.All(box => box.Image is null), "description and pictures belong to the selected software only");
                first = grid.Rows.Cast<DataGridViewRow>().Single(row => (row.Tag as SoftwareItem)?.Id == app.Id); grid.CurrentCell = first.Cells[1];
                Check(SameLines(description.Text, notes) && boxes.All(box => box.Image is not null), "switching back loads saved copies after original images were deleted");
                bool invalidRejected = false;
                Edit(false, "中文别名", notes, dialog => {
                    try { SelectImage(dialog, 0, invalid); }
                    catch (TargetInvocationException) { invalidRejected = true; }
                });
                Check(invalidRejected && new SettingsStore(data).Settings.Apps[app.Id].Picture1 == loaded.Picture1, "invalid image is rejected without replacing the previous picture");
                string source3 = Path.Combine(fixtures, "替换图片.png");
                using (var thirdImage = new Bitmap(100, 80)) { using var canvas = Graphics.FromImage(thirdImage); canvas.Clear(Color.MediumSeaGreen); thirdImage.Save(source3, System.Drawing.Imaging.ImageFormat.Png); }
                int storedBeforeFailure = Directory.GetFiles(Path.Combine(data, "软件图片")).Length;
                using (var blockedSettings = new FileStream(Path.Combine(data, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var errorTimer = new System.Windows.Forms.Timer { Interval = 100 }) {
                    errorTimer.Tick += (_, _) => {
                        IntPtr dialog = FindWindow(null, "操作未完成");
                        if (dialog == IntPtr.Zero) return;
                        GetWindowThreadProcessId(dialog, out uint owner);
                        if (owner != Environment.ProcessId) return;
                        IntPtr ok = GetDlgItem(dialog, 1);
                        if (ok == IntPtr.Zero) ok = GetDlgItem(dialog, 2);
                        if (ok != IntPtr.Zero) { errorTimer.Stop(); PostMessage(ok, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
                    };
                    errorTimer.Start();
                    Edit(true, "不会保存的名称", "不会保存的简介", dialog => SelectImage(dialog, 0, source3));
                }
                var afterFailedSave = new SettingsStore(data).Settings.Apps[app.Id];
                Check(((Label)Field(form, "footer")).Text.StartsWith("操作未完成") && afterFailedSave.DisplayName == "中文别名" && afterFailedSave.Description == notes && afterFailedSave.Picture1 == loaded.Picture1 && afterFailedSave.Picture2 == loaded.Picture2
                    && Directory.GetFiles(Path.Combine(data, "软件图片")).Length == storedBeforeFailure && boxes.All(box => box.Image is not null), "failed settings save rolls back metadata and removes only the new copy");
                string longNotes = string.Join("\r\n", Enumerable.Range(1, 18).Select(number => "第 " + number + " 行简介"));
                Edit(true, "中文别名", longNotes);
                description.Select(description.TextLength, 0); description.ScrollToCaret();
                form.GetType().GetMethod("UpdateSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                Check(description.SelectionStart == description.TextLength && SameLines(description.Text, longNotes), "refresh preserves the viewing position of a long description");
                Edit(true, "中文别名", notes);
                var descriptionBounds = form.RectangleToClient(description.RectangleToScreen(description.ClientRectangle));
                var toggleBounds = form.RectangleToClient(toggle.RectangleToScreen(toggle.ClientRectangle));
                Check(descriptionBounds.Bottom <= toggleBounds.Top, "description and action buttons do not overlap");
                string output = Path.Combine(AppContext.BaseDirectory, "../../..", "记录"); Directory.CreateDirectory(output);
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "简介与操作.png")); }

                toggle.PerformClick();
                await WaitUntil(() => toggle.Text == "停止" && toggle.Enabled && File.Exists(Path.Combine(folder, "窗口就绪.txt")), "real fixture process starts and same button becomes Stop");
                Check(((SoftwareItem)grid.Rows[0].Tag!).Id == app.Id && new SettingsStore(data).Settings.Apps[app.Id].LastLaunchedAt is not null, "successful launch is recorded and sorted first");
                sort.SelectedIndex = 1;
                var sortedNames = grid.Rows.Cast<DataGridViewRow>().Select(row => ((SoftwareItem)row.Tag!).DisplayName).ToArray();
                Check(sortedNames.SequenceEqual(sortedNames.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)) && new SettingsStore(data).Settings.Sort == LibrarySort.Name, "name sort is applied and persisted");
                sort.SelectedIndex = 0;
                Check(((SoftwareItem)grid.Rows[0].Tag!).Id == app.Id && ((SoftwareItem)grid.CurrentRow!.Tag!).Id == app.Id, "recent sorting restores order and keeps selection");
                toggle.PerformClick();
                try { await WaitUntil(() => toggle.Text == "启动" && toggle.Enabled, "same button requests normal exit and returns to Start"); }
                catch {
                    var state = ((ProcessService)Field(form, "processes")).GetState(app.Id);
                    IntPtr dialog = FindWindow(null, "确认强制结束"), window = FindWindow(null, "轻启隔离进程测试");
                    GetWindowThreadProcessId(window, out uint fixturePid);
                    var message = new System.Text.StringBuilder(2048); GetWindowText(GetDlgItem(dialog, 65535), message, message.Capacity);
                    Console.WriteLine($"DIAGNOSTIC button={toggle.Text}, enabled={toggle.Enabled}, processing={Field(form, "processingAction")}, scanning={Field(form, "scanning")}, state={state}, confirmation={dialog != IntPtr.Zero}, fixturePid={fixturePid}, visible={IsWindowVisible(window)}, owner={GetWindow(window, 4)}, text={message}");
                    throw;
                }
                File.Delete(Path.Combine(folder, "窗口就绪.txt"));
                ((SoftwareItem)grid.CurrentRow!.Tag!).Preference.Arguments = "--fixture --block";
                toggle.PerformClick();
                await WaitUntil(() => toggle.Text == "停止" && toggle.Enabled && File.Exists(Path.Combine(folder, "窗口就绪.txt")), "fixture that refuses close starts successfully");
                bool sawConfirmation = false;
                using var confirmationTimer = new System.Windows.Forms.Timer { Interval = 100 };
                confirmationTimer.Tick += (_, _) => {
                    IntPtr dialog = FindWindow(null, "确认强制结束");
                    if (dialog == IntPtr.Zero) return;
                    GetWindowThreadProcessId(dialog, out uint owner);
                    if (owner != Environment.ProcessId) return;
                    IntPtr no = GetDlgItem(dialog, 7);
                    if (no != IntPtr.Zero) { sawConfirmation = true; confirmationTimer.Stop(); PostMessage(no, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
                };
                confirmationTimer.Start(); toggle.PerformClick();
                await WaitUntil(() => sawConfirmation && toggle.Enabled, "refusing normal exit triggers a force-stop confirmation");
                Check(toggle.Text == "停止", "choosing No leaves the tracked process running");
                File.WriteAllText(Path.Combine(folder, "允许退出.txt"), "fixture only");
                await WaitUntil(() => toggle.Text == "启动", "fixture exits normally after confirmation is declined");
                Edit(true, "中文别名", notes, dialog => RemoveImage(dialog, 0));
                loaded = new SettingsStore(data).Settings.Apps[app.Id];
                Check(loaded.Picture1 is null && loaded.Picture2 is not null && boxes[0].Image is null && boxes[1].Image is not null && File.Exists(saved2), "removing one picture preserves the other slot and stored copy");
                Edit(true, "中文别名", "");
                Check(description.Text == "暂无简介" && new SettingsStore(data).Settings.Apps[app.Id].Description is null, "clearing the description persists");
            } catch (Exception error) { failure = error; }
            finally { File.WriteAllText(Path.Combine(folder, "允许退出.txt"), "fixture cleanup"); host.Close(); }
        };
        host.Shown += (_, _) => timer.Start(); Application.Run(host);
        if (failure is not null) throw failure;
        using var reopened = (Form)Activator.CreateInstance(typeof(SettingsStore).Assembly.GetType("LightLaunch.MainForm", true)!, data)!;
        using var secondHost = new Host { ClientSize = reopened.ClientSize, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 100, 100) };
        reopened.TopLevel = false; reopened.FormBorderStyle = FormBorderStyle.None; reopened.MinimumSize = Size.Empty; reopened.Dock = DockStyle.Fill;
        secondHost.Controls.Add(reopened); reopened.Show();
        using var reopenTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        Exception? reopenFailure = null;
        reopenTimer.Tick += (_, _) => {
            reopenTimer.Stop();
            try {
                var pictures = (PictureBox[])Field(reopened, "selectedPictures");
                Check(pictures[0].Image is null && pictures[1].Image is not null && pictures[1].Image!.Width == 120, "reopening the manager restores the remaining picture without original files");
            } catch (Exception error) { reopenFailure = error; }
            finally { secondHost.Close(); }
        };
        secondHost.Shown += (_, _) => reopenTimer.Start(); Application.Run(secondHost);
        if (reopenFailure is not null) throw reopenFailure;
        Console.WriteLine($"Feature checks: {checks} passed. Only isolated fixture software was started and stopped.");
    }
    sealed class Host : Form { protected override bool ShowWithoutActivation => true; }
}
