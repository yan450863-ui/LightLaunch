namespace LightLaunch;

internal sealed class ResidueHistoryDialog : Form
{
    private readonly ResidueHistoryStore history;
    private readonly ResidueService scanner;
    private readonly ResidueCleanupService cleanup;
    private readonly ListBox software = new() { Dock=DockStyle.Fill, BorderStyle=BorderStyle.None, IntegralHeight=false,DrawMode=DrawMode.OwnerDrawFixed };
    private readonly TextBox search = new() { Dock=DockStyle.Fill, PlaceholderText="搜索软件残留" };
    private readonly CheckBox showCleared = new() { Text="显示已处理", AutoSize=true };
    private readonly Button reload = Palette.Button("刷新记录",false,110);
    private readonly Label footer = new() { Dock=DockStyle.Fill, ForeColor=Palette.Muted, AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft };
    private readonly Panel detail = new() { Dock=DockStyle.Fill, Margin=Padding.Empty, BackColor=Palette.Paper };
    private readonly Label empty = new() { Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleCenter, ForeColor=Palette.Muted };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer filterTimer = new(){Interval=350};
    private IReadOnlyList<ResidueHistoryEntry> entries = Array.Empty<ResidueHistoryEntry>();
    private IReadOnlyList<string> warnings = Array.Empty<string>();
    private ResidueDialog? view;
    private string? activeKey;
    private bool loading,rendering,closed,reloadPending;
    public bool IsBusy => view?.IsBusy==true;

    public ResidueHistoryDialog(ResidueHistoryStore history, ResidueService scanner, ResidueCleanupService cleanup)
    {
        this.history=history;this.scanner=scanner;this.cleanup=cleanup;
        Palette.Style(this,"残留记录",new Size(1280,780)); FormBorderStyle=FormBorderStyle.Sizable; MinimumSize=new Size(1100,700);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(22),Margin=Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
        var header=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,120));
        var heading=new Label { Text="残留记录",Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,16,FontStyle.Bold),ForeColor=Palette.Ink };
        reload.Anchor=AnchorStyles.Top|AnchorStyles.Right;header.Controls.Add(heading,0,0);header.Controls.Add(reload,1,0);layout.Controls.Add(header,0,0);
        var body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty };
        body.RowStyles.Add(new RowStyle(SizeType.Percent,100));body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,260));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var side=new SurfacePanel { Dock=DockStyle.Fill,BackColor=Palette.Shell,Padding=new Padding(12),Margin=new Padding(0,0,14,0) };
        var sideLayout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty,BackColor=Palette.Shell };
        sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));sideLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));sideLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        sideLayout.Controls.Add(search);sideLayout.Controls.Add(showCleared);software.BackColor=Palette.Shell;
        sideLayout.Controls.Add(software);side.Controls.Add(sideLayout);body.Controls.Add(side,0,0);
        detail.Controls.Add(empty);body.Controls.Add(detail,1,0);layout.Controls.Add(body,0,1);
        var bottom=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=1,ColumnCount=2,Margin=Padding.Empty,Padding=new Padding(0,10,0,0) };
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        var close=Palette.Button("关闭",false,90);close.DialogResult=DialogResult.Cancel;close.Anchor=AnchorStyles.Right;close.Margin=Padding.Empty;
        bottom.Controls.Add(footer,0,0);bottom.Controls.Add(close,1,0);layout.Controls.Add(bottom,0,2);Controls.Add(layout);CancelButton=close;
        Palette.FinishDialog(this);
        software.ItemHeight=(int)Math.Round(76*DeviceDpi/96f);software.DrawItem+=DrawSoftware;
        search.TextChanged+=(_,_)=>{filterTimer.Stop();filterTimer.Start();};showCleared.CheckedChanged+=(_,_)=>RenderList(true);
        filterTimer.Tick+=(_,_)=>{if(IsBusy)return;filterTimer.Stop();RenderList(true);};
        software.SelectedIndexChanged+=(_,_)=>{if(!rendering)ShowSelected();};reload.Click+=async(_,_)=>{await ReloadAsync();ScheduleInspection(view);};
        footer.Click+=(_,_)=>{if(warnings.Count>0)MessageBox.Show(this,string.Join("\r\n",warnings.Take(20)),"未读取的检测记录",MessageBoxButtons.OK,MessageBoxIcon.Information);};
        Shown+=async(_,_)=>await ReloadAsync();
        FormClosing+=(_,e)=>{if(view?.IsBusy==true){e.Cancel=true;MessageBox.Show(this,"正在检测或清除残留，请等待当前操作完成。","操作正在进行");}};
        FormClosed+=(_,_)=>{closed=true;lifetime.Cancel();};
        empty.Text="正在读取残留记录…";
    }

    private async Task ReloadAsync()
    {
        if(loading||view?.IsBusy==true||closed)return;loading=true;UpdateBusy();
        try {
            var result=await Task.Run(()=>history.Load(),lifetime.Token);if(closed)return;
            entries=result.Entries;warnings=result.Warnings;RenderList();
        }catch(OperationCanceledException) { }
        catch(Exception ex){if(!closed){footer.Text="记录读取未完成："+ex.Message;MessageBox.Show(this,ex.Message,"残留记录",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        finally {loading=false;if(!closed)UpdateBusy();}
    }

    private void RenderList(bool inspectSelected=false)
    {
        if(closed||view?.IsBusy==true)return;
        string? selected=(software.SelectedItem as ResidueHistoryEntry)?.Key??activeKey;
        string query=search.Text.Trim();
        var shown=entries.Where(e=>showCleared.Checked||e.NeedsAttention).Where(e=>query.Length==0||
            (e.Report.Snapshot.SoftwareName+" "+e.Report.Snapshot.Registration.DisplayName+" "+e.Report.Snapshot.Registration.Publisher+" "+e.Report.Snapshot.OriginalFolderPath).Contains(query,StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e=>e.Report.Completion==UninstallCompletionState.Completed&&e.Report.Findings.Count>0).ToArray();
        rendering=true;software.BeginUpdate();software.Items.Clear();software.Items.AddRange(shown.Cast<object>().ToArray());
        if(shown.Length>0)software.SelectedIndex=Math.Max(0,Array.FindIndex(shown,e=>e.Key==selected));
        software.EndUpdate();rendering=false;
        footer.Text=$"{shown.Length} 款软件"+(warnings.Count==0?"":" · "+warnings.Count+" 份记录无法读取");
        ShowSelected(inspectSelected);
    }

    private void ShowSelected(bool inspect=false)
    {
        if(view?.IsBusy==true)return;
        var selected=software.SelectedItem as ResidueHistoryEntry;
        if(selected is not null&&selected.Key==activeKey&&view is not null&&selected.Report.ScannedAt==view.CurrentReport.ScannedAt
            &&selected.Report.Snapshot.CapturedAt==view.CurrentReport.Snapshot.CapturedAt){if(inspect)ScheduleInspection(view);return;}
        if(view is not null){detail.Controls.Remove(view);view.Dispose();view=null;}
        activeKey=selected?.Key;
        if(selected is null){empty.Text=entries.Count==0?"暂无记录":"没有匹配的软件";empty.Visible=true;return;}
        empty.Visible=false;
        string path=selected.Path;
        view=new ResidueDialog(selected.Report,async token=>{
            var result=await Task.Run(()=>scanner.Scan(selected.Report.Snapshot,cancellationToken:token),token);
            path=history.Save(result,path);reloadPending=true;return result;
        },cleanup,true){Dock=DockStyle.Fill};
        view.BusyChanged+=(_,_)=>{
            UpdateBusy();
            if(view?.IsBusy==false&&reloadPending&&!closed){reloadPending=false;BeginInvoke(new Action(async()=>await ReloadAsync()));}
        };
        detail.Controls.Add(view);view.Show();ScheduleInspection(view);
    }

    private void ScheduleInspection(ResidueDialog? target)
    {
        if(target is null||closed||!IsHandleCreated)return;
        BeginInvoke(new Action(async()=>{if(!closed&&ReferenceEquals(view,target)&&!target.IsBusy)await target.RefreshForSelectionAsync();}));
    }

    private void UpdateBusy()
    {
        bool enabled=!loading&&view?.IsBusy!=true;
        software.Enabled=search.Enabled=showCleared.Enabled=reload.Enabled=enabled;
        if(view is not null)view.Enabled=!loading;
    }

    private void DrawSoftware(object? sender,DrawItemEventArgs e)
    {
        if(e.Index<0||software.Items[e.Index] is not ResidueHistoryEntry item)return;
        bool selected=(e.State&DrawItemState.Selected)!=0;float scale=DeviceDpi/96f;
        using(var background=new SolidBrush(Palette.Shell))e.Graphics.FillRectangle(background,e.Bounds);
        var bounds=e.Bounds;bounds.Inflate(-(int)(2*scale),-(int)(3*scale));
        if(selected){e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using var shape=Palette.Rounded(bounds,8*scale);using var fill=new SolidBrush(Palette.AccentSoft);e.Graphics.FillPath(fill,shape);}
        int inset=(int)(10*scale),line=(int)(29*scale);
        var name=new Rectangle(bounds.X+inset,bounds.Y+(int)(5*scale),bounds.Width-inset*2,line);
        TextRenderer.DrawText(e.Graphics,item.Report.Snapshot.SoftwareName,Font,name,selected?Palette.Accent:Palette.Ink,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter);
        var state=new Rectangle(name.X,name.Bottom,name.Width,line);
        using var stateFont=new Font(Font.FontFamily,9);
        TextRenderer.DrawText(e.Graphics,item.State,stateFont,state,Palette.Muted,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter);
        if((e.State&DrawItemState.Focus)!=0&&software.Focused)e.DrawFocusRectangle();
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing&&!closed){closed=true;lifetime.Cancel();}
        if(disposing){filterTimer.Stop();filterTimer.Dispose();view?.Dispose();lifetime.Dispose();}
        base.Dispose(disposing);
    }
}
