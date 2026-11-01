namespace LightLaunch;

internal sealed class RenameDialog : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly TextBox description = new() { Dock = DockStyle.Fill, MaxLength = 500, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
    private readonly PictureChoice?[] pictures = new PictureChoice?[2];
    private readonly PictureBox[] pictureBoxes = new PictureBox[2];
    private readonly SoftwarePictures pictureStore;
    public string ChosenName => name.Text;
    public string ChosenDescription => description.Text.Trim();
    public PictureChoice?[] ChosenPictures => (PictureChoice?[])pictures.Clone();
    public RenameDialog(SoftwareItem item, SoftwarePictures pictureStore)
    {
        this.pictureStore = pictureStore;
        Palette.Style(this, "编辑软件资料", new Size(600, 570));
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 7, ColumnCount = 1 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        body.Controls.Add(new Label { Text = "软件显示名称", Dock = DockStyle.Fill });
        name.Text = item.DisplayName; body.Controls.Add(name);
        body.Controls.Add(new Label { Text = "软件简介", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        description.Text = item.Preference.Description ?? ""; body.Controls.Add(description);
        body.Controls.Add(new Label { Text = "软件图片（最多两张）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        var imageRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        imageRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); imageRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        string?[] existing = { item.Preference.Picture1, item.Preference.Picture2 };
        for (int index = 0; index < 2; index++) {
            int slot = index;
            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, index == 0 ? 8 : 0, 0) };
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Palette.SurfaceAlt, Margin = Padding.Empty, AccessibleName = "图片 " + (index + 1) };
            preview.Paint += (_, e) => { if (preview.Image is null) TextRenderer.DrawText(e.Graphics, "未选择图片", Font, preview.ClientRectangle, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
            pictureBoxes[index] = preview; frame.Controls.Add(preview);
            var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            var choose = Palette.Button("选择图片"); choose.Dock = DockStyle.Fill; choose.Margin = new Padding(0, 4, 4, 0);
            var remove = Palette.Button("移除"); remove.Dock = DockStyle.Fill; remove.Margin = new Padding(4, 4, 0, 0);
            choose.Click += (_, _) => ChoosePicture(slot); remove.Click += (_, _) => RemovePicture(slot);
            controls.Controls.Add(choose, 0, 0); controls.Controls.Add(remove, 1, 0); frame.Controls.Add(controls);
            imageRows.Controls.Add(frame, index, 0);
            if (!string.IsNullOrEmpty(existing[index])) { pictures[index] = new(existing[index], null, null); preview.Image = pictureStore.TryLoad(existing[index], 400); }
        }
        body.Controls.Add(imageRows);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Palette.Button("保存", true); save.DialogResult = DialogResult.OK;
        var cancel = Palette.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        var restore = Palette.Button("恢复原名"); restore.Click += (_, _) => name.Text = item.Name;
        buttons.Controls.AddRange(new Control[] { save, cancel, restore }); body.Controls.Add(buttons);
        Controls.Add(body); AcceptButton = save; CancelButton = cancel; Palette.FinishDialog(this);
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var preview in pictureBoxes) if (preview is not null) { var image = preview.Image; preview.Image = null; image?.Dispose(); }
        base.Dispose(disposing);
    }

    private void ChoosePicture(int slot)
    {
        using var picker = new OpenFileDialog { Title = "选择软件图片", Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { SetPictureFromFile(slot, picker.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { MessageBox.Show(this, ex.Message, "无法读取图片", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SetPictureFromFile(int slot, string path)
    {
        var selection = SoftwarePictures.ReadSelection(path);
        var bitmap = SoftwarePictures.Decode(selection.Bytes!, 400);
        var previous = pictureBoxes[slot].Image; pictureBoxes[slot].Image = bitmap; previous?.Dispose();
        pictures[slot] = selection; pictureBoxes[slot].Invalidate();
    }

    private void RemovePicture(int slot)
    {
        pictures[slot] = null;
        var previous = pictureBoxes[slot].Image; pictureBoxes[slot].Image = null; previous?.Dispose();
        pictureBoxes[slot].Invalidate();
    }
}
