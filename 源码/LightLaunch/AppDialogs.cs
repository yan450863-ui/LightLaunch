namespace LightLaunch;

internal sealed class EntryDialog : Form
{
    private readonly ListBox entries = new();
    private readonly TextBox arguments = new();
    private readonly TextBox working = new();
    private readonly SoftwareItem item;
    public string ChosenEntry { get; private set; } = "";
    public string Arguments => arguments.Text;
    public string? WorkingDirectory => string.IsNullOrWhiteSpace(working.Text) ? null : working.Text.Trim();

    public EntryDialog(SoftwareItem item)
    {
        this.item = item;
        Palette.Style(this, "设置入口 · " + item.Name, new Size(690, 450));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (int h in new[] { 28, 36, 28, 36, 52 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        body.Controls.Add(new Label { Text = "启动入口", Dock = DockStyle.Fill });
        entries.Dock = DockStyle.Fill; entries.BorderStyle = BorderStyle.FixedSingle; entries.IntegralHeight = false;
        foreach (string path in item.CandidateEntries) entries.Items.Add(Path.GetRelativePath(item.FolderPath, path));
        string? current = item.Preference.EntryRelativePath;
        for (int i = 0; i < entries.Items.Count; i++) if (string.Equals(current, entries.Items[i]?.ToString(), StringComparison.OrdinalIgnoreCase)) entries.SelectedIndex = i;
        body.Controls.Add(entries);
        body.Controls.Add(new Label { Text = "参数（可选）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft });
        arguments.Dock = DockStyle.Fill; arguments.Text = item.Preference.Arguments; body.Controls.Add(arguments);
        body.Controls.Add(new Label { Text = "工作目录（相对路径，可选）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft });
        working.Dock = DockStyle.Fill; working.Text = item.Preference.WorkingDirectoryRelative ?? ""; body.Controls.Add(working);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = Palette.Button("保存入口", true); save.Click += (_, _) => SaveEntry();
        var cancel = Palette.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var help=Palette.Button("使用说明");help.Click+=(_,_)=>GuideDialog.Open(this);
        buttons.Controls.AddRange(new Control[] { save, cancel,help }); body.Controls.Add(buttons);
        Controls.Add(body); AcceptButton = save; CancelButton = cancel; Palette.FinishDialog(this);
        entries.DoubleClick += (_, _) => SaveEntry();
    }
    private void SaveEntry()
    {
        if (entries.SelectedItem is not string chosen) { MessageBox.Show(this, "请先选择启动入口。", "选择入口"); return; }
        string full = Path.GetFullPath(Path.Combine(item.FolderPath, chosen));
        if (!LibraryService.IsSafeEntryPath(item.FolderPath, full)) { MessageBox.Show(this, "该入口不可用，或包含指向软件目录外的链接。", "选择入口"); return; }
        if (WorkingDirectory is string relative)
        {
            string folder;
            try { folder = Path.GetFullPath(Path.Combine(item.FolderPath, relative)); }
            catch { MessageBox.Show(this, "工作目录格式无效。", "选择入口"); return; }
            if (Path.IsPathRooted(relative) || (!string.Equals(folder, item.FolderPath, StringComparison.OrdinalIgnoreCase) && !LibraryService.IsPathInside(item.FolderPath, folder)) || !Directory.Exists(folder))
            { MessageBox.Show(this, "工作目录必须是软件文件夹内存在的相对目录。", "选择入口"); return; }
        }
        ChosenEntry = chosen; DialogResult = DialogResult.OK;
    }
}

internal sealed class UninstallDialog : Form
{
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ListBox registrations = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Palette.Surface };
    private readonly List<UninstallRegistration> options;
    public AppKind ChosenKind => kind.SelectedIndex == 1 ? AppKind.Portable : kind.SelectedIndex == 2 ? AppKind.Installed : AppKind.Unknown;
    public UninstallRegistration? Registration => ChosenKind == AppKind.Installed && registrations.SelectedItem is RegistrationOption option ? option.Value : null;
    private sealed record RegistrationOption(UninstallRegistration Value)
    { public override string ToString() => Value.DisplayName + (string.IsNullOrEmpty(Value.Publisher) ? "" : "  ·  " + Value.Publisher); }

    public UninstallDialog(SoftwareItem item, List<UninstallRegistration> candidates, List<UninstallRegistration> all)
        :this(item,candidates,all,UninstallService.ProposeDefault(item,all)){}

    public UninstallDialog(SoftwareItem item,List<UninstallRegistration> candidates,List<UninstallRegistration> all,UninstallProposal proposal)
    {
        options = candidates.ToList();
        string? selectedRegistryId = proposal.Registration?.Id;
        if (proposal.Registration is { } proposed && !options.Any(value => value.Id == proposed.Id)) options.Insert(0, proposed);
        Palette.Style(this, "确认卸载方式 · " + item.Name, new Size(750, 560));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        foreach (int h in new[] { 30, 38, 36, 32 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (int h in new[] { 30, 112, 52 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        body.Controls.Add(new Label { Text = proposal.Kind==AppKind.Unknown?"请选择卸载方式":"卸载方式", Dock = DockStyle.Fill });
        kind.Items.AddRange(new object[] { "待确认", "文件夹回收", "正规卸载程序" });
        body.Controls.Add(kind);
        var explanation = new Label { Dock = DockStyle.Fill, ForeColor = Palette.Muted, Text = proposal.Kind==AppKind.Unknown?"未找到唯一匹配，请核对。":proposal.Kind==AppKind.Portable?"回收整个软件文件夹。":"将调用所选软件的卸载程序。" };
        var tip=new ToolTip();tip.SetToolTip(explanation,proposal.Reason);FormClosed+=(_,_)=>tip.Dispose();
        body.Controls.Add(explanation);
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        filterRow.Controls.Add(new Label{Text="匹配程序",AutoSize=true});body.Controls.Add(filterRow);
        registrations.SelectedIndexChanged += (_, _) => { if (Registration is { } selected) selectedRegistryId = selected.Id; UpdateDetails(); }; body.Controls.Add(registrations);
        var detailsLabel=new Label { Text = "匹配记录详情", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };body.Controls.Add(detailsLabel);
        body.Controls.Add(details);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var confirm = Palette.Button("继续卸载", true); confirm.Click += (_, _) => {
            if (ChosenKind == AppKind.Unknown) { MessageBox.Show(this, "请先确认软件类型。", "确认类型"); return; }
            if (ChosenKind == AppKind.Installed && Registration == null) { MessageBox.Show(this, "请选择并核对一条 Windows 卸载记录。", "选择卸载记录"); return; }
            DialogResult = DialogResult.OK;
        };
        var cancel = Palette.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var help=Palette.Button("使用说明");help.Click+=(_,_)=>GuideDialog.Open(this);
        buttons.Controls.AddRange(new Control[] { confirm, cancel,help }); body.Controls.Add(buttons);
        Controls.Add(body); CancelButton = cancel;
        bool layoutReady = false;
        int Unit(int value) => (int)Math.Round(value * (layoutReady ? DeviceDpi / 96f : 1));
        kind.SelectedIndexChanged += (_, _) => {
            registrations.Enabled = details.Enabled = ChosenKind == AppKind.Installed;
            bool show=ChosenKind!=AppKind.Portable&&options.Count>0;
            filterRow.Visible=registrations.Visible=detailsLabel.Visible=details.Visible=show;
            foreach(int row in new[]{3,4,5,6}){body.RowStyles[row].SizeType=row==4&&show?SizeType.Percent:SizeType.Absolute;body.RowStyles[row].Height=show?row==4?100:Unit(row switch{3=>32,5=>30,_=>112}):0;}
            body.RowStyles[2].SizeType=show?SizeType.Absolute:SizeType.Percent;body.RowStyles[2].Height=show?Unit(36):100;
            ClientSize=new Size(Unit(750),Unit(show?560:260));UpdateDetails();
        };
        kind.SelectedIndex = proposal.Kind == AppKind.Portable ? 1 : proposal.Kind == AppKind.Installed ? 2 : 0;
        Populate(options, selectedRegistryId);
        Palette.FinishDialog(this);
        layoutReady = true;
    }
    private void Populate(List<UninstallRegistration> values, string? selected)
    {
        registrations.Items.Clear();
        foreach (var value in values) registrations.Items.Add(new RegistrationOption(value));
        for (int i = 0; i < registrations.Items.Count; i++) if (((RegistrationOption)registrations.Items[i]).Value.Id == selected) registrations.SelectedIndex = i;
        UpdateDetails();
    }
    private void UpdateDetails()
    {
        var r = Registration;
        details.Text = r == null ? ChosenKind==AppKind.Portable?"将整个软件文件夹移入回收站，包含其中的配置和个人数据。":"尚未匹配到唯一卸载记录。" : $"名称：{r.DisplayName}\r\n发布者：{r.Publisher ?? "未提供"}\r\n安装目录：{r.InstallLocation ?? "未提供"}\r\n卸载命令：{r.Command}";
    }
}
