namespace LightLaunch;

internal sealed class MigrationDialog : Form
{
    readonly ComboBox products=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly TextBox original=new(){Dock=DockStyle.Fill};
    readonly CheckBox keepCompatibility=new(){Text="保留原位置兼容入口（仅在软件依赖旧路径时勾选）",Dock=DockStyle.Fill,Checked=false};
    public bool KeepCompatibility=>keepCompatibility.Checked;
    public UninstallRegistration? Registration => (products.SelectedItem as Option)?.Value;
    public string OriginalPath=>original.Text.Trim();
    sealed record Option(UninstallRegistration Value) {public override string ToString()=>Value.DisplayName+" · "+(Value.Publisher??"发布者未填写");}
    public MigrationDialog(SoftwareItem item,IEnumerable<UninstallRegistration> candidates,IEnumerable<UninstallRegistration> all)
    {
        Palette.Style(this,"关联剪切迁入的安装版",new Size(940,520));
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=8};
        foreach(int height in new[]{56,24,38,24,38,24,38})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.Controls.Add(new Label{Text="请先把整个软件文件夹剪切到软件库。默认不保留旧入口；卸载时临时提供旧路径，卸载进程组结束后清理。关联不会自动启动软件。",Dock=DockStyle.Fill});
        layout.Controls.Add(new Label{Text="当前真实软件目录",Dock=DockStyle.Fill});
        layout.Controls.Add(new TextBox{Text=item.FolderPath,ReadOnly=true,Dock=DockStyle.Fill});
        layout.Controls.Add(new Label{Text="原 Windows 卸载记录（名称候选需人工核对）",Dock=DockStyle.Fill});
        var ordered=candidates.Concat(all).DistinctBy(r=>r.Id).ToArray();
        products.Items.AddRange(ordered.Select(r=>(object)new Option(r)).ToArray());layout.Controls.Add(products);
        layout.Controls.Add(new Label{Text="原安装目录（应已被剪切移走，原父目录仍存在）",Dock=DockStyle.Fill});layout.Controls.Add(original);
        var bottom=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1};
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,38));bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100));bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        bottom.Controls.Add(keepCompatibility);
        bottom.Controls.Add(new Label{Text="当前支持有明确原目录证据的普通桌面软件；共享安装目录、服务、驱动及未知链接不自动处理。请核对记录对应的就是此软件。",Dock=DockStyle.Fill,ForeColor=Palette.Muted});
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0)};
        var next=Palette.Button("核对并关联",true,145);var cancel=Palette.Button("取消",false,90);cancel.DialogResult=DialogResult.Cancel;
        next.Click+=(_,_)=>{if(Registration is null||string.IsNullOrWhiteSpace(OriginalPath)){MessageBox.Show(this,"请选择原卸载记录和原安装目录。");return;}DialogResult=DialogResult.OK;};
        buttons.Controls.AddRange(new Control[]{next,cancel});bottom.Controls.Add(buttons);layout.Controls.Add(bottom);Controls.Add(layout);
        products.SelectedIndexChanged+=(_,_)=>original.Text=Registration is{} registration?MigrationService.SuggestOriginalDirectory(registration)??"":"";
        if(candidates.Any()&&products.Items.Count>0)products.SelectedIndex=0;
        AcceptButton=next;CancelButton=cancel;Palette.FinishDialog(this);
    }
}
