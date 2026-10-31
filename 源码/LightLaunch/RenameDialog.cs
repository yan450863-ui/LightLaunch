namespace LightLaunch;

internal sealed class RenameDialog : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    public string ChosenName => name.Text;
    public RenameDialog(SoftwareItem item)
    {
        Palette.Style(this, "重命名", new Size(460, 210));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, ColumnCount = 1 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(new Label { Text = "软件显示名称", Dock = DockStyle.Fill });
        name.Text = item.DisplayName; body.Controls.Add(name);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Palette.Button("保存", true); save.DialogResult = DialogResult.OK;
        var cancel = Palette.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var restore = Palette.Button("恢复原名"); restore.Click += (_, _) => name.Text = item.Name;
        buttons.Controls.AddRange(new Control[] { save, cancel, restore }); body.Controls.Add(buttons);
        Controls.Add(body); AcceptButton = save; CancelButton = cancel; Palette.FinishDialog(this);
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
