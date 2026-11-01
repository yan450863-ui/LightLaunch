using System.Security.Cryptography;

namespace LightLaunch;

internal sealed class ShortcutRepairDialog : Form
{
    private sealed record Candidate(string Shortcut, string OldTarget, string NewTarget, string OldIcon, string OldWorking, byte[] Hash);
    private readonly List<Candidate> candidates = new();
    private readonly CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly SoftwareItem[] items;
    private readonly LauncherSettings settings;
    private readonly MigrationService migration;
    private readonly string dataDirectory;
    private readonly string[] desktopDirectories;

    public ShortcutRepairDialog(SoftwareItem[] items, LauncherSettings settings, MigrationService migration, string dataDirectory, string[]? desktopDirectories = null)
    {
        this.items = items; this.settings = settings; this.migration = migration; this.dataDirectory = dataDirectory;
        this.desktopDirectories = desktopDirectories ?? new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        Text = "桌面快捷方式检测与修复"; Size = new Size(820, 480); MinimumSize = new Size(620, 360);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = Palette.Button("关闭", false, 90); close.Click += (_, _) => Close();
        var repair = Palette.Button("修复所选", true, 110); repair.Click += (_, _) => Repair();
        var create = Palette.Button("创建桌面快捷方式", false, 160); create.Click += (_, _) => CreateShortcut();
        var scan = Palette.Button("重新检测", false, 100); scan.Click += (_, _) => Scan();
        buttons.Controls.AddRange(new Control[] { close, repair, create, scan }); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); Shown += (_, _) => Scan();
    }

    private static dynamic Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string IconPath(string value)
    {
        int comma = value.LastIndexOf(',');
        return (comma >= 0 && int.TryParse(value[(comma + 1)..], out _) ? value[..comma] : value).Trim().Trim('"');
    }
    private static string? Rebase(string path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Same(path, oldRoot)) return newRoot;
            if (!LibraryService.IsPathInside(oldRoot, path)) return null;
            return Path.GetFullPath(Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private void Scan()
    {
        candidates.Clear(); list.Items.Clear(); int skipped = 0;
        dynamic shell = Shell();
        foreach (string desktop in desktopDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(desktop)) continue;
            foreach (string file in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                try
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) { skipped++; continue; }
                    dynamic link = shell.CreateShortcut(file);
                    string target = link.TargetPath, icon = link.IconLocation, working = link.WorkingDirectory;
                    if (string.IsNullOrWhiteSpace(target) || target.StartsWith("\\\\")) continue;
                    var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.DiscoveryWarning)) continue;
                        foreach (var old in settings.FolderIdentities.Concat(settings.PreviousLocations).Where(p => p.Value == item.Id).Select(p => p.Key))
                        {
                            string? mapped = Rebase(target, old, item.StoragePath);
                            if (mapped is not null && File.Exists(mapped)) matches.Add(mapped);
                        }
                        // A previous library root is established only by another known item identity.
                        if (settings.Layout == LibraryLayout.Direct && settings.LibraryRoot is string currentRoot)
                        {
                            foreach (var known in settings.FolderIdentities.Where(p => items.Any(i => i.Id == p.Value)))
                            {
                                string? previousRoot = Path.GetDirectoryName(known.Key);
                                if (previousRoot is null || Same(previousRoot, currentRoot)) continue;
                                string? mapped = Rebase(target, previousRoot, currentRoot);
                                if (mapped is not null && (Same(mapped, item.StoragePath) || LibraryService.IsPathInside(item.StoragePath, mapped)) && File.Exists(mapped)) matches.Add(mapped);
                            }
                        }
                        try
                        {
                            var record = migration.GetForItem(item);
                            if (record is not null && !item.IsSingleFile)
                            {
                                string? mapped = Rebase(target, record.OriginalPath, item.FolderPath);
                                if (mapped is not null && File.Exists(mapped)) matches.Add(mapped);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                    }
                    string? newTarget = matches.Count == 1 ? matches.Single() : null;
                    if (newTarget is null) continue;
                    string iconPath = IconPath(icon);
                    bool iconMissing = !string.IsNullOrWhiteSpace(iconPath) && !iconPath.StartsWith("%") && !File.Exists(iconPath);
                    if (File.Exists(target) && !iconMissing) continue;
                    var candidate = new Candidate(file, target, newTarget, icon, working, SHA256.HashData(File.ReadAllBytes(file)));
                    candidates.Add(candidate); list.Items.Add(Path.GetFileNameWithoutExtension(file) + "：" + target + "  →  " + newTarget, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { skipped++; }
            }
        }
        status.Text = $"发现 {candidates.Count} 个可明确匹配的快捷方式，勾选后修复" + (skipped > 0 ? $"；{skipped} 个无法读取" : "");
    }

    private void Repair()
    {
        var selected = list.CheckedIndices.Cast<int>().Select(i => candidates[i]).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "请先勾选要修复的快捷方式。", Text); return; }
        if (MessageBox.Show(this, $"将修复 {selected.Length} 个桌面快捷方式，并备份原文件。继续吗？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int success = 0; var errors = new List<string>(); dynamic shell = Shell();
        foreach (var c in selected)
        {
            try
            {
                RepairOne(c, shell); success++;
            }
            catch (Exception ex) { errors.Add(Path.GetFileName(c.Shortcut) + "：" + ex.Message); }
        }
        MessageBox.Show(this, $"已修复 {success} 个。" + (errors.Count > 0 ? "\r\n" + string.Join("\r\n", errors) : ""), Text);
        Scan();
    }

    private void RepairOne(Candidate c, dynamic shell)
    {
        if (!File.Exists(c.NewTarget) || !SHA256.HashData(File.ReadAllBytes(c.Shortcut)).SequenceEqual(c.Hash)) throw new IOException("快捷方式或目标已变化，请重新检测。");
        string backup = Path.Combine(dataDirectory, "快捷方式备份", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup); File.Copy(c.Shortcut, Path.Combine(backup, Path.GetFileName(c.Shortcut)));
        dynamic link = shell.CreateShortcut(c.Shortcut);
        link.TargetPath = c.NewTarget;
        if (!string.IsNullOrWhiteSpace(c.OldWorking) && !Directory.Exists(c.OldWorking))
        {
            string? oldDir = Path.GetDirectoryName(c.OldTarget), newDir = Path.GetDirectoryName(c.NewTarget);
            if (oldDir is not null && newDir is not null && Same(oldDir, c.OldWorking)) link.WorkingDirectory = newDir;
        }
        string iconPath = IconPath(c.OldIcon);
        if (!string.IsNullOrWhiteSpace(iconPath) && !iconPath.StartsWith("%") && !File.Exists(iconPath)) link.IconLocation = c.NewTarget + ",0";
        link.Save();
    }

    private void CreateShortcut()
    {
        using var picker = new Form { Text = "选择软件", Size = new Size(480, 400), StartPosition = FormStartPosition.CenterParent };
        var choices = new ListBox { Dock = DockStyle.Fill, DisplayMember = "DisplayName" };
        choices.Items.AddRange(items.Where(x => x.EntryPath is not null && File.Exists(x.EntryPath) && string.IsNullOrEmpty(x.DiscoveryWarning)).Cast<object>().ToArray());
        var ok = new Button { Text = "创建", Dock = DockStyle.Bottom, Height = 40, DialogResult = DialogResult.OK };
        picker.Controls.Add(choices); picker.Controls.Add(ok); picker.AcceptButton = ok;
        if (picker.ShowDialog(this) != DialogResult.OK || choices.SelectedItem is not SoftwareItem item || item.EntryPath is null) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string name = string.Concat(item.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(desktop, name + ".lnk");
            if (File.Exists(path)) throw new IOException("桌面上已有同名快捷方式，请先重命名已有文件。");
            dynamic link = Shell().CreateShortcut(path); link.TargetPath = item.EntryPath;
            link.Arguments = item.Preference.Arguments; link.WorkingDirectory = item.FolderPath; link.IconLocation = item.EntryPath + ",0"; link.Save();
            MessageBox.Show(this, "已创建桌面快捷方式。", Text);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
