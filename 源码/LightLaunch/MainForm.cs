using System.Diagnostics;

namespace LightLaunch;

internal sealed class MainForm : Form
{
    private readonly SettingsStore store;
    private readonly LibraryService library;
    private readonly ProcessService processes = new();
    private readonly UninstallService uninstaller = new();
    private readonly ResidueService residue = new();
    private readonly MigrationService migration;
    private readonly ResidueCleanupService residueCleanup;
    private readonly ResidueHistoryStore residueHistoryStore;
    private readonly List<SoftwareItem> items = new();
    private readonly Dictionary<string, TrackedProcessState> states = new();
    private readonly Dictionary<string, Image> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string AppId, FolderSizeResult Value)> sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DataGridView grid = new();
    private readonly ListBox categories = new();
    private readonly TextBox search = new();
    private readonly Label rootLabel = new();
    private readonly Label footer = new();
    private readonly Label selectedName = new();
    private readonly Label selectedPath = new();
    private readonly Label selectedEntry = new();
    private readonly Label selectedSize = new();
    private readonly Label empty = new();
    private readonly Label listSummary = new();
    private readonly Button chooseRoot = Palette.Button("选择软件根目录", false, 164);
    private readonly Button refresh = Palette.Button("刷新列表", false, 105);
    private readonly Button launch = Palette.Button("启动", true, 86);
    private readonly Button stop = Palette.Button("停止", false, 86);
    private readonly Button open = Palette.Button("打开文件夹", false, 126);
    private readonly Button entry = Palette.Button("设置入口", false, 108);
    private readonly Button measure = Palette.Button("刷新大小", false, 108);
    private readonly Button uninstall = Palette.Button("卸载", false, 86);
    private readonly Button residueHistory = Palette.Button("残留记录", false, 126);
    private readonly System.Windows.Forms.Timer watchTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer processTimer = new() { Interval = 1000 };
    private FileSystemWatcher? watcher;
    private bool scanning, polling, processingAction, updatingGrid, watcherDirty, closed, rootAvailable;
    private bool migrationUninstallBusy;
    private ResidueHistoryDialog? activeResidueHistory;
    private volatile bool watcherError;
    private DateTime lastChange, firstChange, lastPeriodicScan;
    private string? watcherFailure;
    private int scanGeneration;
    private readonly object changesLock = new();
    private readonly HashSet<string> changedPaths = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CategoryOption(string? Name, int Count)
    { public override string ToString() => (Name ?? "全部软件") + "   " + Count; }

    public MainForm(string dataDirectory)
    {
        SuspendLayout();
        store = new SettingsStore(dataDirectory); library = new LibraryService(store.Settings);
        migration = new MigrationService(dataDirectory);
        residueCleanup=new ResidueCleanupService(dataDirectory);
        residueHistoryStore = new ResidueHistoryStore(Path.Combine(Path.GetFullPath(dataDirectory), "卸载检测"));
        Text = "轻启 · 软件管理器"; ClientSize = new Size(1220, 760); MinimumSize = new Size(1090, 620);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); BackColor = Palette.Paper; ForeColor = Palette.Ink;
        BuildUi();
        ResumeLayout(true);
        ApplyListMetrics();
        DpiChanged += (_, _) => ApplyListMetrics();
        Load += (_, _) => Palette.FitWindow(this);
        Shown += async (_, _) => {
            if (store.LoadWarning is string warning) MessageBox.Show(this, warning, "配置提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!string.IsNullOrWhiteSpace(store.Settings.LibraryRoot)) { ConfigureWatcher(); await ScanAsync(); }
            else SetEmpty("选择软件根目录", "点击右上角选择目录。");
            watchTimer.Start(); processTimer.Start();
        };
        FormClosing += (_, e) => {
            if(activeResidueHistory?.IsBusy==true){e.Cancel=true;MessageBox.Show(this,"正在检测或清除残留，请等待当前操作完成。","操作正在进行");return;}
            if(migrationUninstallBusy) {e.Cancel=true;MessageBox.Show(this,"卸载器及子进程尚未完成，请先在卸载器中完成或取消操作。","卸载正在进行",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        };
        FormClosed += (_, _) => {
            closed = true; lifetime.Cancel(); watchTimer.Stop(); processTimer.Stop(); watcher?.Dispose();
            processes.Dispose(); foreach (var icon in icons.Values) icon.Dispose();
            watchTimer.Dispose(); processTimer.Dispose(); lifetime.Dispose();
        };
        watchTimer.Tick += async (_, _) => {
            bool due;
            lock (changesLock) due = watcherDirty && (DateTime.UtcNow - lastChange > TimeSpan.FromMilliseconds(1000) || DateTime.UtcNow - firstChange > TimeSpan.FromSeconds(4));
            if (!scanning && !processingAction && (due || DateTime.UtcNow - lastPeriodicScan > TimeSpan.FromSeconds(40)))
            { if (store.Settings.LibraryRoot != null) await ScanAsync(); }
        };
        processTimer.Tick += async (_, _) => await PollProcessesAsync();
        chooseRoot.Click += async (_, _) => await ChooseRootAsync();
        refresh.Click += async (_, _) => await ScanAsync();
        launch.Click += async (_, _) => await LaunchAsync(); stop.Click += async (_, _) => await StopAsync();
        open.Click += (_, _) => OpenFolder(); entry.Click += (_, _) => ChooseEntry();
        measure.Click += async (_, _) => { if (Selected is { } app) await MeasureOneAsync(app, true); };
        uninstall.Click += async (_, _) => await UninstallAsync();
    }

    private void BuildUi()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Shell, Margin = Padding.Empty };
        header.Paint += (_, e) => { using var pen = new Pen(Palette.Border); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        var headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 12, 26, 12), ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var brand = new BrandMark { Size = new Size(42, 42), Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 0, 20, 0) };
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        var headingLine = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var title = new Label { Text = "轻启", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
        headingLine.Controls.Add(title); heading.Controls.Add(headingLine, 0, 0);
        rootLabel.Dock = DockStyle.Fill; rootLabel.Margin = Padding.Empty; rootLabel.ForeColor = Palette.Muted; rootLabel.AutoEllipsis = true;
        rootLabel.Font = new Font(Font.FontFamily, 9); rootLabel.Text = "尚未关联软件根目录";
        heading.Controls.Add(rootLabel, 0, 1); chooseRoot.Anchor = AnchorStyles.Right; chooseRoot.Margin = Padding.Empty;
        headerLayout.Controls.Add(brand, 0, 0); headerLayout.Controls.Add(heading, 1, 0); headerLayout.Controls.Add(chooseRoot, 2, 0);
        header.Controls.Add(headerLayout); layout.Controls.Add(header, 0, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 12, 26, 10), ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 472));
        var searchFrame = new SurfacePanel(9) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0), Padding = new Padding(12, 9, 12, 5), BorderColor = Palette.BorderStrong };
        search.Dock = DockStyle.Fill; search.BorderStyle = BorderStyle.None; search.BackColor = Palette.Surface;
        search.PlaceholderText = "搜索软件名称、分类或路径"; search.AccessibleName = "搜索软件";
        search.TextChanged += (_, _) => RenderRows();
        search.Enter += (_, _) => { searchFrame.BorderColor = Palette.Accent; searchFrame.Invalidate(); };
        search.Leave += (_, _) => { searchFrame.BorderColor = Palette.BorderStrong; searchFrame.Invalidate(); };
        searchFrame.Controls.Add(search); toolbar.Controls.Add(searchFrame, 0, 0);
        var utilities = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        refresh.Width = 100; residueHistory.Width = 110;
        var systemApps = Palette.Button("系统应用", false, 110);
        var help = Palette.Button("使用说明",false,100);help.Click+=(_,_)=>GuideDialog.Open(this);
        systemApps.Click += (_, _) => RunUi(() => uninstaller.OpenWindowsAppsSettings());
        residueHistory.Click += (_, _) => RunUi(ShowResidueHistory);
        utilities.Controls.AddRange(new Control[] { refresh, residueHistory, systemApps,help }); toolbar.Controls.Add(utilities, 1, 0);
        layout.Controls.Add(toolbar, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(26, 0, 26, 16), Margin = Padding.Empty };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var side = new SurfacePanel { Dock = DockStyle.Fill, BackColor = Palette.Shell, Padding = new Padding(10, 16, 10, 14), Margin = new Padding(0, 0, 18, 0) };
        var categoryTitle = new Label { Text = "软件分类", ForeColor = Palette.Muted, Height = 34, Dock = DockStyle.Top, Padding = new Padding(10, 0, 0, 0), Font = new Font(Font.FontFamily, 9) };
        categories.Dock = DockStyle.Fill; categories.BorderStyle = BorderStyle.None; categories.BackColor = Palette.Shell;
        categories.DrawMode = DrawMode.OwnerDrawFixed; categories.ItemHeight = 46; categories.IntegralHeight = false;
        categories.DrawItem += DrawCategory;
        categories.SelectedIndexChanged += (_, _) => { if (!updatingGrid) RenderRows(); };
        side.Controls.Add(categories); side.Controls.Add(categoryTitle); body.Controls.Add(side, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        var listCard = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12), Margin = Padding.Empty };
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Palette.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var listHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(8, 0, 8, 0) };
        listHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        listHeading.Controls.Add(new Label { Text = "软件库", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 12, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        listSummary.Dock = DockStyle.Fill; listSummary.TextAlign = ContentAlignment.MiddleRight; listSummary.ForeColor = Palette.Muted; listSummary.Font = new Font(Font.FontFamily, 9);
        listHeading.Controls.Add(listSummary, 1, 0); listLayout.Controls.Add(listHeading, 0, 0);
        var listPanel = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Margin = Padding.Empty };
        grid.Dock = DockStyle.Fill; grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.MultiSelect = false;
        grid.ReadOnly = true; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoGenerateColumns = false;
        Palette.StyleGrid(grid); grid.RowTemplate.Height = 58;
        grid.Columns.Add(new DataGridViewImageColumn { Name = "Icon", HeaderText = "", Width = 52, ImageLayout = DataGridViewImageCellLayout.Zoom, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AppName", HeaderText = "软件名称", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "分类", Width = 108 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "文件夹大小", Width = 128, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "运行状态", Width = 166 });
        grid.Columns[1].DefaultCellStyle.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        grid.Columns[2].DefaultCellStyle.ForeColor = Palette.Muted;
        grid.SelectionChanged += (_, _) => { if (!updatingGrid) UpdateSelection(); };
        grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await LaunchAsync(); };
        grid.CellPainting += PaintStatus;
        empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter; empty.Font = new Font(Font.FontFamily, 11); empty.ForeColor = Palette.Muted;
        listPanel.Controls.Add(grid); listPanel.Controls.Add(empty); listLayout.Controls.Add(listPanel, 0, 1);
        listCard.Controls.Add(listLayout); content.Controls.Add(listCard, 0, 0);

        var detail = new SurfacePanel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(20, 16, 20, 14) };
        var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (int h in new[] { 28, 23, 23, 23 }) detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedName.Font = new Font(Font.FontFamily, 12, FontStyle.Bold);
        foreach (var label in new[] { selectedName, selectedPath, selectedEntry, selectedSize }) {
            label.Dock = DockStyle.Fill; label.Margin = Padding.Empty; label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft; detailLayout.Controls.Add(label);
        }
        foreach (var label in new[] { selectedPath, selectedEntry, selectedSize }) { label.ForeColor = Palette.Muted; label.Font = new Font(Font.FontFamily, 9); }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        launch.Width = 104; stop.Width = 76; open.Width = 118; entry.Width = 98; measure.Width = 98; uninstall.Width = 76;
        actions.Controls.AddRange(new Control[] { launch, stop, open, entry, measure, uninstall });
        detailLayout.Controls.Add(actions); detail.Controls.Add(detailLayout); content.Controls.Add(detail, 0, 1);
        body.Controls.Add(content, 1, 0); layout.Controls.Add(body, 0, 2);

        footer.Dock = DockStyle.Fill; footer.Padding = new Padding(28, 0, 16, 0); footer.TextAlign = ContentAlignment.MiddleLeft;
        footer.BackColor = Palette.Shell; footer.ForeColor = Palette.Muted; footer.Font = new Font(Font.FontFamily, 8.5f);
        footer.Paint += (_, e) => { using var pen = new Pen(Palette.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        footer.Text = "就绪"; layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout); UpdateSelection();
    }

    private void DrawCategory(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || categories.Items[e.Index] is not CategoryOption option) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(Palette.Shell)) e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = e.Bounds; bounds.Inflate(-2, -3);
        if (selected) {
            using var shape = Palette.Rounded(bounds, 8 * DeviceDpi / 96f); using var fill = new SolidBrush(Palette.AccentSoft); e.Graphics.FillPath(fill, shape);
        }
        int inset = 10 * DeviceDpi / 96, countWidth = 28 * DeviceDpi / 96;
        TextRenderer.DrawText(e.Graphics, option.Name ?? "全部软件", Font,
            new Rectangle(bounds.X + inset, bounds.Y, Math.Max(0, bounds.Width - countWidth - inset * 2), bounds.Height),
            selected ? Palette.Accent : Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, option.Count.ToString(), Font,
            new Rectangle(bounds.Right - countWidth - inset, bounds.Y, countWidth, bounds.Height),
            selected ? Palette.Accent : Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        if ((e.State & DrawItemState.Focus) != 0 && categories.Focused) e.DrawFocusRectangle();
    }

    private void ApplyListMetrics()
    {
        float scale = DeviceDpi / 96f;
        categories.ItemHeight = (int)Math.Round(46 * scale);
        grid.ColumnHeadersHeight = (int)Math.Round(40 * scale);
        grid.RowTemplate.Height = (int)Math.Round(58 * scale);
        foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
        foreach (var (name, width) in new[] { ("Icon", 52), ("Category", 108), ("Size", 128), ("Status", 166) })
            grid.Columns[name]!.Width = (int)Math.Round(width * scale);
        grid.Columns["AppName"]!.MinimumWidth = (int)Math.Round(150 * scale);
    }

    private void PaintStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 4 || e.Value is not string text) return;
        e.PaintBackground(e.ClipBounds, true);
        var color = grid.Rows[e.RowIndex].Cells[4].Style.ForeColor;
        var fillColor = color == Palette.Warn ? Palette.WarnSoft : color == Palette.Accent ? Palette.AccentSoft : Palette.SurfaceAlt;
        var bounds = e.CellBounds; int inset = 8 * DeviceDpi / 96;
        bounds.Inflate(-inset, -14 * DeviceDpi / 96);
        e.Graphics!.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var shape = Palette.Rounded(bounds, 7 * DeviceDpi / 96f); using var fill = new SolidBrush(fillColor); e.Graphics.FillPath(fill, shape);
        TextRenderer.DrawText(e.Graphics, text, grid.Font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.Handled = true;
    }

    private SoftwareItem? Selected => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as SoftwareItem : null;
    private void SetStatus(string message) { if (!closed) footer.Text = message; }
    private void SetEmpty(string title, string detail) { empty.Text = title + "\r\n\r\n" + detail; empty.Visible = true; grid.Visible = false; }
    private void RunUi(Action action) { try { action(); } catch (Exception ex) { ShowError(ex); } }
    private void ShowError(Exception ex) { if (!closed) { SetStatus("操作未完成 · " + ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    private void Save() => store.Save();

    private async Task ChooseRootAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择软件根目录", UseDescriptionForTitle = true, ShowNewFolderButton = true, SelectedPath = store.Settings.LibraryRoot ?? "" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        store.Settings.LibraryRoot = Path.GetFullPath(dialog.SelectedPath); sizes.Clear();
        try { Save(); } catch (Exception ex) { ShowError(ex); }
        ConfigureWatcher(); await ScanAsync();
    }
    private void ConfigureWatcher()
    {
        watcher?.Dispose(); watcher = null;
        var root = store.Settings.LibraryRoot; if (string.IsNullOrEmpty(root)) return;
        try {
            if (!Directory.Exists(root)) return;
            watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 32768 };
            FileSystemEventHandler change = (_, e) => MarkChange(e.FullPath);
            watcher.Created += change; watcher.Deleted += change; watcher.Changed += change;
            watcher.Renamed += (_, e) => { MarkChange(e.FullPath); MarkChange(e.OldFullPath); };
            watcher.Error += (_, e) => { watcherError = true; watcherFailure = e.GetException().Message; MarkChange(root); };
            watcher.EnableRaisingEvents = true;
        } catch (Exception ex) { watcherError = true; watcherFailure = ex.Message; }
    }
    private void MarkChange(string path)
    {
        lock (changesLock) { if (!watcherDirty) firstChange = DateTime.UtcNow; lastChange = DateTime.UtcNow; watcherDirty = true; changedPaths.Add(path); }
    }
    private async Task ScanAsync()
    {
        var root = store.Settings.LibraryRoot; if (scanning || closed || string.IsNullOrWhiteSpace(root)) return;
        scanning = true; lastPeriodicScan = DateTime.UtcNow; refresh.Enabled = chooseRoot.Enabled = false; UpdateSelection(); int generation = ++scanGeneration;
        SetStatus("正在检查软件目录…");
        try {
            HashSet<string> changed;
            lock (changesLock) { changed = new HashSet<string>(changedPaths, StringComparer.OrdinalIgnoreCase); changedPaths.Clear(); watcherDirty = false; }
            if (watcherError) sizes.Clear();
            foreach (var key in sizes.Keys.ToArray()) if (changed.Any(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || string.Equals(path, key, StringComparison.OrdinalIgnoreCase) || LibraryService.IsPathInside(key, path) || LibraryService.IsPathInside(path, key))) sizes.Remove(key);
            var result = await Task.Run(() => library.Scan(root, lifetime.Token)); if (closed) return;
            rootLabel.Text = root; chooseRoot.Text = "更换软件根目录";
            rootAvailable = result.RootAvailable;
            if (!result.RootAvailable) {
                if (result.Items.Count > 0 || items.Count == 0) { items.Clear(); items.AddRange(result.Items); }
                RenderCategories(); RenderRows();
                SetStatus("根目录不可用 · 保留已有列表与配置，目录恢复后会重试");
                if (items.Count == 0) SetEmpty("根目录暂时不可用", root + "\r\n已有配置已保留，请检查磁盘或目录后刷新。");
                launch.Enabled = uninstall.Enabled = false; return;
            }
            items.Clear(); items.AddRange(result.Items);
            foreach (var app in items) {
                if (sizes.TryGetValue(app.FolderPath, out var cached) && cached.AppId == app.Id && DateTime.Now - cached.Value.CalculatedAt < TimeSpan.FromSeconds(60)) ApplySize(app, cached.Value);
                else { sizes.Remove(app.FolderPath); app.SizeBytes = null; app.SizeCalculatedAt = null; app.SizeIncomplete = false; }
            }
            Save(); RenderCategories(); RenderRows();
            SetStatus(result.Warnings.Count>0?result.Warnings[0]:$"已更新 · {DateTime.Now:HH:mm}");
            if (watcherError || watcher == null) { watcherError = false; ConfigureWatcher(); }
            if (watcherError) SetStatus("目录监听暂不可用，40 秒补扫仍有效 · " + watcherFailure);
            lastPeriodicScan = DateTime.UtcNow;
            _ = MeasureMissingAsync(generation);
        } catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { scanning = false; lastPeriodicScan = DateTime.UtcNow; if (!closed) { refresh.Enabled = chooseRoot.Enabled = true; UpdateSelection(); } }
    }
    private void RenderCategories()
    {
        string? previous = (categories.SelectedItem as CategoryOption)?.Name;
        updatingGrid = true; categories.Items.Clear(); categories.Items.Add(new CategoryOption(null, items.Count));
        foreach (var g in items.GroupBy(x => x.Category).OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)) categories.Items.Add(new CategoryOption(g.Key, g.Count()));
        categories.SelectedIndex = 0;
        for (int i = 1; i < categories.Items.Count; i++) if (((CategoryOption)categories.Items[i]).Name == previous) categories.SelectedIndex = i;
        updatingGrid = false;
    }
    private void RenderRows()
    {
        if (closed) return;
        string? selectedId = Selected?.Id; string? category = (categories.SelectedItem as CategoryOption)?.Name;
        string query = search.Text.Trim();
        var visible = items.Where(x => category == null || x.Category == category).Where(x => query.Length == 0 || (x.Name + " " + x.Category + " " + x.FolderPath).Contains(query, StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        listSummary.Text = $"{visible.Length} 个软件" + (category is null ? " · 全部分类" : " · " + category);
        updatingGrid = true; grid.Rows.Clear();
        foreach (var app in visible) {
            int index = grid.Rows.Add(GetIcon(app), app.Name, app.Category, SizeText(app), StatusText(app)); var row = grid.Rows[index]; row.Tag = app;
            row.Cells[1].ToolTipText = app.FolderPath; row.Cells[4].ToolTipText = app.DiscoveryWarning ?? "仅跟踪由管理器启动的程序；外部启动的程序可能未被跟踪。";
            ColorStatus(row, app);
        }
        grid.ClearSelection();
        var selectedRow = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => (x.Tag as SoftwareItem)?.Id == selectedId) ?? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (selectedRow != null) { selectedRow.Selected = true; grid.CurrentCell = selectedRow.Cells[1]; }
        updatingGrid = false;
        if (items.Count == 0) SetEmpty("暂无软件", "将软件文件夹放入分类目录。");
        else if (visible.Length == 0) SetEmpty("没有匹配的软件", "");
        else { empty.Visible = false; grid.Visible = true; }
        UpdateSelection();
    }
    private Image GetIcon(SoftwareItem app)
    {
        string key = app.EntryPath ?? app.FolderPath; if (icons.TryGetValue(key, out var value)) return value;
        try { if (app.EntryPath is string p && File.Exists(p)) { using var icon = Icon.ExtractAssociatedIcon(p); if (icon != null) return icons[key] = icon.ToBitmap(); } } catch { }
        return icons[key] = SystemIcons.Application.ToBitmap();
    }
    private string StatusText(SoftwareItem app)
    {
        if (states.TryGetValue(app.Id, out var state)) { if (state.IsRunning) return state.IsUncertain ? "运行中 · 部分未知" : $"运行中 · {state.ProcessCount} 进程"; if (state.IsUncertain) return "未知 · 未完全跟踪"; }
        if (app.DiscoveryWarning != null) return "目录需检查";
        if (app.EntryPath == null) return app.CandidateEntries.Count == 0 ? "未找到启动入口" : "待选择主程序";
        return "未跟踪运行";
    }
    private static string FormatBytes(long bytes) { string[] units = { "B", "KB", "MB", "GB", "TB" }; double size = bytes; int i = 0; while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; } return $"{size:0.#} {units[i]}"; }
    private static string SizeText(SoftwareItem app) => app.SizeBytes is long b ? (app.SizeIncomplete ? "≥ " : "") + FormatBytes(b) : "计算中…";
    private void ColorStatus(DataGridViewRow row, SoftwareItem app) => row.Cells[4].Style.ForeColor = states.TryGetValue(app.Id, out var s) && s.IsRunning && !s.IsUncertain ? Palette.Accent : app.EntryPath == null || app.DiscoveryWarning is not null || s?.IsUncertain == true ? Palette.Warn : Palette.Muted;
    private void UpdateSelection()
    {
        if (closed) return;
        residueHistory.Enabled = !scanning && !processingAction;
        chooseRoot.Enabled = refresh.Enabled = !scanning && !processingAction;
        var app = Selected; bool has = app != null, canEdit = has && rootAvailable && !scanning && !processingAction;
        foreach (var b in new[] { launch, stop, open, entry, measure, uninstall }) b.Enabled = canEdit;
        if (app == null) { selectedName.Text = "选择软件"; selectedPath.Text = selectedEntry.Text = selectedSize.Text = ""; return; }
        selectedName.Text = app.Name; selectedPath.Text = "位置：" + app.FolderPath;
        selectedEntry.Text = app.EntryPath == null ? "入口：未设置" : "入口：" + app.Preference.EntryRelativePath + (app.Preference.Arguments.Length == 0 ? "" : "  " + app.Preference.Arguments);
        selectedSize.Text = app.SizeCalculatedAt is DateTime ? $"大小：{SizeText(app)}" + (app.SizeIncomplete ? "（统计不完整）" : "") : "大小：计算中…";
        if (app.DiscoveryWarning is string w) selectedSize.Text += " · " + w;
        stop.Enabled = has && !processingAction && states.TryGetValue(app.Id, out var state) && state.IsRunning;
        entry.Enabled = canEdit && app.CandidateEntries.Count > 0;
    }
    private async Task PollProcessesAsync()
    {
        if (polling || closed || items.Count == 0) return; polling = true;
        try {
            string[] ids = items.Select(x => x.Id).Distinct().ToArray();
            var values = await Task.Run(() => ids.ToDictionary(x => x, x => processes.GetState(x))); if (closed) return;
            foreach (var pair in values) states[pair.Key] = pair.Value;
            foreach (DataGridViewRow row in grid.Rows) if (row.Tag is SoftwareItem app) { row.Cells[4].Value = StatusText(app); ColorStatus(row, app); }
            UpdateSelection();
        } catch (Exception ex) { SetStatus("进程状态暂不可用 · " + ex.Message); }
        finally { polling = false; }
    }
    private static void ApplySize(SoftwareItem app, FolderSizeResult value) { app.SizeBytes = value.Bytes; app.SizeIncomplete = value.Incomplete; app.SizeCalculatedAt = value.CalculatedAt; }
    private async Task MeasureMissingAsync(int generation)
    {
        foreach (var app in items.ToArray()) {
            if (closed || generation != scanGeneration) return;
            if (app.SizeBytes == null) await MeasureOneAsync(app, false, generation);
        }
    }
    private async Task MeasureOneAsync(SoftwareItem app, bool userRequested, int? expectedGeneration = null)
    {
        int generation = expectedGeneration ?? scanGeneration;
        try {
            var result = await Task.Run(() => library.CalculateSize(app.FolderPath, lifetime.Token)); if (closed || generation != scanGeneration) return;
            var current = items.FirstOrDefault(x => x.Id == app.Id && x.FolderPath == app.FolderPath); if (current == null) return;
            sizes[app.FolderPath] = (app.Id, result);
            ApplySize(current, result); foreach (DataGridViewRow row in grid.Rows) if ((row.Tag as SoftwareItem)?.Id == app.Id) row.Cells[3].Value = SizeText(current);
            UpdateSelection(); if (userRequested) SetStatus("大小已刷新 · " + app.Name + (result.Incomplete ? " · 统计不完整" : ""));
        } catch (OperationCanceledException) { }
        catch (Exception ex) { if (userRequested) ShowError(ex); }
    }
    private bool ChooseEntry(SoftwareItem? target = null)
    {
        var app = target ?? Selected; if (app == null || app.CandidateEntries.Count == 0) return false;
        bool ownsAction = !processingAction;
        if (ownsAction) { processingAction = true; UpdateSelection(); }
        try {
            using var dialog = new EntryDialog(app); if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            app.Preference.EntryRelativePath = dialog.ChosenEntry; app.Preference.Arguments = dialog.Arguments; app.Preference.WorkingDirectoryRelative = dialog.WorkingDirectory;
            Save(); RenderRows(); SetStatus("已保存启动入口 · " + app.Name); return true;
        } catch (Exception ex) { ShowError(ex); return false; }
        finally { if (ownsAction) { processingAction = false; UpdateSelection(); } }
    }
    private async Task LaunchAsync()
    {
        var app = Selected; if (app == null || processingAction || scanning) return;
        processingAction = true; UpdateSelection();
        try {
            if (app.EntryPath == null && !ChooseEntry(app)) return;
            string? root = store.Settings.LibraryRoot;
            if (root == null || !LibraryService.IsCurrentSoftwareItem(root, app))
                throw new InvalidOperationException("目标软件目录已变化，请刷新列表后重新选择；本次没有启动其他软件。");
            var status = await Task.Run(() => processes.GetState(app.Id));
            if (status.IsRunning) { MessageBox.Show(this, "此软件已由本次管理器启动。首版每个软件同时跟踪一个运行组，请使用已经打开的窗口。", "软件正在运行", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            await Task.Run(() => processes.Launch(app)); SetStatus("已请求启动 · " + app.Name); await PollProcessesAsync();
        } catch (Exception ex) { ShowError(ex); }
        finally { processingAction = false; UpdateSelection(); }
    }
    private async Task StopAsync()
    {
        var app = Selected; if (app == null || processingAction) return;
        processingAction = true; UpdateSelection();
        try {
            SetStatus("已请求正常退出 · 请处理软件的保存提示");
            var result = await processes.RequestStopAsync(app.Id, lifetime.Token);
            if (!result.Completed && result.CanForce) {
                var choice = MessageBox.Show(this, result.Message + "\r\n\r\n软件可能正在等待保存。是否强制结束已跟踪的进程？未保存内容可能丢失。\r\n选择“否”会保留程序运行。", "确认强制结束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (choice == DialogResult.Yes) { await Task.Run(() => processes.ForceStop(app.Id)); SetStatus("已请求结束已跟踪进程 · " + app.Name); }
                else SetStatus("已保留软件运行，可处理保存提示后再次停止");
            } else SetStatus(result.Message);
            await PollProcessesAsync();
        } catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { processingAction = false; UpdateSelection(); }
    }
    private void OpenFolder()
    {
        if (Selected is not { } app) return;
        RunUi(() => { if (!Directory.Exists(app.FolderPath)) throw new DirectoryNotFoundException("软件文件夹已不存在，请刷新列表。"); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { app.FolderPath }, UseShellExecute = true }); });
    }
    private async Task UninstallAsync()
    {
        var app = Selected; var root = store.Settings.LibraryRoot;
        if (app == null || string.IsNullOrEmpty(root) || processingAction || scanning) return;
        processingAction = true; UpdateSelection();
        try {
            var status = await Task.Run(() => processes.GetState(app.Id));
            if (status.IsRunning || status.IsUncertain) { MessageBox.Show(this, "请先停止此软件。若跟踪状态未知，请确认进程已退出后重新打开管理器。", "软件仍在运行或状态未知", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if(await HandlePreviousMigrationAsync(root,app))return;
            SetStatus("正在识别当前软件的卸载方式…");
            var registrations = await Task.Run(() => uninstaller.GetRegistrations());
            var detection=await Task.Run(()=>new SoftwareDetectionService().Detect(app,registrations,migration.GetForItem(app)));
            using var dialog = new UninstallDialog(app,detection.Candidates.ToList(),registrations,detection.Proposal); if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string operation = dialog.ChosenKind == AppKind.Portable ? $"将整个软件文件夹移入回收站：\r\n{app.FolderPath}\r\n\r\n其中的配置和个人数据也会移走。" : $"启动以下软件的正规卸载程序：\r\n软件：{app.Name}\r\n软件目录：{app.FolderPath}\r\n卸载记录：{dialog.Registration!.DisplayName}\r\n登记命令：{dialog.Registration.Command}";
            if (MessageBox.Show(this, operation + "\r\n\r\n确认继续？", "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (!LibraryService.IsCurrentSoftwareItem(root, app)) throw new InvalidOperationException("目标软件目录已变化，请刷新并重新核对卸载对象。");
            if(dialog.ChosenKind==AppKind.Installed&&migration.GetForItem(app) is null) {
                var selected=dialog.Registration!;
                string? original=MigrationService.SuggestOriginalDirectory(selected)??(detection.Proposal.Registration?.Id==selected.Id?detection.OriginalDirectory:null);
                if(original is null&&app.EntryPath is{} main)original=await Task.Run(()=>{var evidence=MsiEntryReader.Find(selected,main);return evidence is null?null:Path.GetDirectoryName(evidence.OriginalPath);});
                if(original is not null&&!Path.GetFullPath(original).Equals(Path.GetFullPath(app.FolderPath),StringComparison.OrdinalIgnoreCase)&&!LibraryService.IsPathInside(app.FolderPath,original)) {
                    if(Directory.Exists(original)||File.Exists(original))throw new IOException("此登记指向另一个仍存在的安装目录，本次不会卸载其他位置的软件。请核对当前文件夹的回收方式。");
                    if(app.EntryPath is null&&!ChooseEntry(app))return;
                    var plan=await Task.Run(()=>migration.Prepare(root,app,selected,original));
                    await Task.Run(()=>migration.Commit(plan));
                }
            }
            if (migration.GetForItem(app) is not null) {
                if (dialog.ChosenKind != AppKind.Installed) throw new IOException("已关联的迁入安装版需要先执行正规卸载；登记移除后才可确认回收剩余目录。");
                await Task.Run(() => migration.ValidateForUninstall(root, app, dialog.Registration!));
            }
            app.Preference.Kind = dialog.ChosenKind; app.Preference.UninstallRegistryId = dialog.Registration?.Id; Save();
            if (dialog.ChosenKind == AppKind.Portable) { await Task.Run(() => uninstaller.RecyclePortable(root, app)); SetStatus("已移入回收站 · " + app.Name); }
            else {
                var snapshot = await Task.Run(() => residue.CaptureSnapshot(app, dialog.Registration!), lifetime.Token);
                var pending = new ResidueScanResult(snapshot, DateTimeOffset.Now, UninstallCompletionState.Pending, false,
                    "等待正规卸载完成；可稍后从残留记录重新检测。", Array.Empty<ResidueFinding>(), Array.Empty<ResidueCheck>(), Array.Empty<string>());
                string reportPath = SaveResidueReport(pending);
                TrackedUninstall? tracked=null;
                bool associated=migration.GetForItem(app) is not null,temporary=false,started=false,groupEnded=false;
                var observation = new UninstallObservation();
                try {
                    migrationUninstallBusy=true;
                    if(associated) {
                        temporary=migration.GetForItem(app)?.State=="Detached";
                        await Task.Run(()=>migration.BeginUninstallCompatibility(root,app,dialog.Registration!));
                    }
                    tracked=uninstaller.BeginTrackedUninstall(dialog.Registration!);
                    started=true;
                    SetStatus("正规卸载程序运行中 · 完成后自动检测残留");
                    int code=await tracked.WaitForCompletionAsync(lifetime.Token);groupEnded=true;
                    observation = new(code == 1602 ? UninstallRunState.Cancelled
                        : code is 0 or 3010 or 1641 ? UninstallRunState.Exited : UninstallRunState.Failed,code);
                }
                catch (Exception launchError) {
                    var state = launchError is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                        ? UninstallRunState.Cancelled : UninstallRunState.Failed;
                    var failed = await Task.Run(() => residue.Scan(snapshot, new UninstallObservation(state)));
                    SaveResidueReport(failed, reportPath);
                    if (!closed) ShowResidueReport(failed, reportPath);
                    throw;
                }
                finally {
                    try {
                        if(temporary&&(!started||groupEnded)) {
                            // An early launcher exit cannot remove a path still used by its child uninstaller.
                            var state=migration.RecoverInterrupted(root,app);
                            if(state?.State=="TemporaryActive")await Task.Run(()=>migration.RemoveCompatibility(root,app,true));
                        }
                    }finally{tracked?.Dispose();migrationUninstallBusy=false;}
                }
                await Task.Delay(600, lifetime.Token);
                var result = await Task.Run(() => residue.Scan(snapshot, observation, lifetime.Token), lifetime.Token);
                if (!closed && observation.State == UninstallRunState.Exited && migration.GetForItem(app) is not null
                    && Directory.Exists(app.FolderPath) && Directory.EnumerateFileSystemEntries(app.FolderPath).Any()
                    && await Task.Run(() => migration.CanRecycleRemainder(root, app))) {
                    var answer = MessageBox.Show(this, "原产品卸载登记已移除，迁入目录仍有文件。请先确认正规卸载器已经结束。\r\n\r\n是否将本次关联的整个迁入目录移入回收站？其中的配置和个人数据也会移走。\r\n" + app.FolderPath,
                        "迁入软件剩余文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer == DialogResult.Yes) await Task.Run(() => migration.RecycleRemainder(root, app));
                }
                string migrationResult = await Task.Run(() => migration.FinishUninstall(root, app));
                result = await Task.Run(() => residue.Scan(snapshot, observation, lifetime.Token), lifetime.Token);
                if (!string.IsNullOrEmpty(migrationResult)) result = result with { Summary = result.Summary + "\r\n" + migrationResult };
                SaveResidueReport(result, reportPath);
                if (!closed) { SetStatus(result.Summary); ShowResidueReport(result, reportPath); }
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed) ShowError(ex); }
        finally { processingAction = false; if (!closed) { UpdateSelection(); await ScanAsync(); } }
    }

    private async Task<bool> HandlePreviousMigrationAsync(string root,SoftwareItem app)
    {
        var existing=await Task.Run(()=>migration.RecoverInterrupted(root,app));
        if(existing is null)return false;
        if(existing.State=="TemporaryActive") {
            if(MessageBox.Show(this,"上次卸载中断。请确认卸载器及其所有子窗口已经关闭，再继续清理本管理器建立的临时入口。","核对卸载状态",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return true;
            await Task.Run(()=>migration.RemoveCompatibility(root,app,true));existing=migration.GetForItem(app)!;
        }
        if(!await Task.Run(()=>migration.CanRecycleRemainder(root,app)))return false;
        if(MessageBox.Show(this,"原产品登记已移除。是否将剩余软件文件夹移入回收站？其中的配置和个人数据也会移走。\r\n"+app.FolderPath,"清理卸载剩余文件",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return true;
        var snapshot=ResidueSnapshot.Create(app.Name,existing.TargetPath,Path.Combine(existing.TargetPath,existing.MainRelativePath),existing.Registration);
        await Task.Run(()=>migration.RecycleRemainder(root,app));
        string message=await Task.Run(()=>migration.FinishUninstall(root,app));
        var report=await Task.Run(()=>residue.Scan(snapshot));string path=SaveResidueReport(report);SetStatus(message);ShowResidueReport(report,path);return true;
    }
    private string SaveResidueReport(ResidueScanResult result, string? path = null)
        => residueHistoryStore.Save(result,path);

    private void ShowResidueReport(ResidueScanResult result, string? reportPath = null)
    {
        using var dialog = new ResidueDialog(result, async cancellationToken => {
            var updated = await Task.Run(() => residue.Scan(result.Snapshot, cancellationToken: cancellationToken), cancellationToken);
            reportPath = SaveResidueReport(updated, reportPath);
            return updated;
        },residueCleanup);
        dialog.ShowDialog(this);
    }

    private void ShowResidueHistory()
    {
        if (processingAction || scanning || closed) return;
        using var dialog = new ResidueHistoryDialog(residueHistoryStore,residue,residueCleanup);
        activeResidueHistory=dialog;
        try {dialog.ShowDialog(this);} finally {activeResidueHistory=null;}
    }
}
