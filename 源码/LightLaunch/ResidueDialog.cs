using System.Diagnostics;

namespace LightLaunch;

internal sealed class ResidueDialog : Form
{
    private readonly DataGridView results = new();
    private readonly Label summary = new() { Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft };
    private readonly Label selection = new() { AutoSize=true,ForeColor=Palette.Muted,Margin=new Padding(16,5,0,0) };
    private readonly CheckBox showChecks = new() { Text="检查明细",AutoSize=true };
    private readonly CheckBox selectAll = new() { Text="全选可删除项",AutoSize=true };
    private readonly Button rescan = Palette.Button("重新检测",false,104);
    private readonly Button clean = Palette.Button("删除所选",true,104);
    private readonly ToolTip tips = new();
    private readonly ResidueCleanupService? cleanup;
    private readonly Func<CancellationToken,Task<ResidueScanResult>> refreshResult;
    private readonly CancellationTokenSource lifetime = new();
    private ResidueScanResult current;
    private bool closed,busy,disposed,verified,rendering;
    public bool IsBusy => busy;
    public ResidueScanResult CurrentReport => current;
    public event EventHandler? BusyChanged;

    public ResidueDialog(ResidueScanResult result,Func<CancellationToken,Task<ResidueScanResult>> refreshResult):this(result,refreshResult,null) { }
    public ResidueDialog(ResidueScanResult result,Func<CancellationToken,Task<ResidueScanResult>> refreshResult,ResidueCleanupService? cleanup)
        :this(result,refreshResult,cleanup,false) { }

    public ResidueDialog(ResidueScanResult result,Func<CancellationToken,Task<ResidueScanResult>> refreshResult,ResidueCleanupService? cleanup,bool embedded)
    {
        current=result;this.refreshResult=refreshResult;this.cleanup=cleanup;verified=!embedded;
        Palette.Style(this,"残留检测 · "+result.Snapshot.SoftwareName,new Size(1080,620));
        FormBorderStyle=FormBorderStyle.Sizable;MinimumSize=new Size(940,500);
        if(embedded){TopLevel=false;FormBorderStyle=FormBorderStyle.None;MinimumSize=Size.Empty;ShowInTaskbar=false;}
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(embedded?16:22),Margin=Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,62));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60));
        summary.Font=new Font(Font.FontFamily,11);layout.Controls.Add(summary,0,0);
        var options=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty };
        options.Controls.AddRange(new Control[]{selectAll,showChecks,selection});layout.Controls.Add(options,0,1);
        results.Dock=DockStyle.Fill;results.ReadOnly=false;results.EditMode=DataGridViewEditMode.EditOnEnter;
        results.AllowUserToAddRows=false;results.AllowUserToDeleteRows=false;results.AllowUserToResizeRows=false;
        results.RowHeadersVisible=false;results.MultiSelect=false;results.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        results.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;results.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
        results.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText="选择",Width=55,AutoSizeMode=DataGridViewAutoSizeColumnMode.None });
        results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="类型",FillWeight=18,ReadOnly=true });
        results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="位置",FillWeight=60,ReadOnly=true });
        results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="状态",FillWeight=24,ReadOnly=true });
        results.Columns[3].DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        results.CurrentCellDirtyStateChanged+=(_,_)=>{if(results.IsCurrentCellDirty)results.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        results.CellValueChanged+=(_,e)=>{if(!rendering&&e.ColumnIndex==0)UpdateActions();};
        layout.Controls.Add(results,0,2);
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0),Margin=Padding.Empty,WrapContents=false };
        var close=Palette.Button("关闭",false,90);close.DialogResult=DialogResult.OK;close.Visible=!embedded;
        var help=Palette.Button("使用说明",false,104);help.Click+=(_,_)=>GuideDialog.Open(this);
        var copy=Palette.Button("复制路径",false,104);copy.Click+=(_,_)=>CopyLocation();
        var open=Palette.Button("打开位置",false,104);open.Click+=(_,_)=>OpenLocation();
        rescan.Click+=async(_,_)=>await RefreshAsync();clean.Click+=async(_,_)=>await CleanAsync();
        buttons.Controls.AddRange(new Control[]{close,help,copy,open,rescan,clean});layout.Controls.Add(buttons,0,3);
        Controls.Add(layout);if(!embedded)CancelButton=close;
        showChecks.CheckedChanged+=(_,_)=>Render();
        selectAll.CheckedChanged+=(_,_)=>{
            if(rendering||busy)return;rendering=true;results.EndEdit();
            foreach(DataGridViewRow row in results.Rows)if(!row.Cells[0].ReadOnly)row.Cells[0].Value=selectAll.Checked;
            rendering=false;UpdateActions();
        };
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;};FormClosed+=(_,_)=>{closed=true;lifetime.Cancel();};
        Palette.FinishDialog(this);Render();
    }

    private void Render()
    {
        rendering=true;results.Rows.Clear();selectAll.Checked=false;
        int allowed=cleanup is null?0:current.Findings.Count(f=>ResidueCleanupService.Eligibility(current,f).Allowed);
        string state=!verified?"等待检测":current.Completion!=UninstallCompletionState.Completed?"卸载未完成，暂不能删除"
            :current.Findings.Count==0?(current.ScanComplete?"未发现残留":"部分位置无法读取")
            :$"发现 {current.Findings.Count} 项残留 · {allowed} 项可删除";
        summary.Text=current.Snapshot.SoftwareName+"\r\n"+state;
        tips.SetToolTip(summary,current.Summary+"\r\n"+string.Join("\r\n",current.Warnings));
        if(showChecks.Checked) {
            foreach(var check in current.Checks) {
                string label=check.Presence switch{ResiduePresence.Present=>"存在",ResiduePresence.Absent=>"不存在",ResiduePresence.SkippedLink=>"已跳过链接",_=>"无法读取"};
                var row=results.Rows[results.Rows.Add(false,SourceLabel(check.Source),check.Location,label)];
                row.Tag=check.Location;row.Cells[0].ReadOnly=true;row.Cells[2].ToolTipText=check.Location;row.Cells[3].ToolTipText=check.Reason;
            }
        } else foreach(var finding in current.Findings) {
            var eligibility=ResidueCleanupService.Eligibility(current,finding);
            bool can=verified&&cleanup is not null&&eligibility.Allowed;
            string label=!verified?"检测后可选":!eligibility.Allowed?(current.Completion!=UninstallCompletionState.Completed?"需先完成卸载":"需核对")
                :finding.Confidence==ResidueConfidence.PossibleShared?"可删除 · 含配置":"可删除";
            var row=results.Rows[results.Rows.Add(false,SourceLabel(finding.Source),finding.Location,label)];row.Tag=finding;
            row.Cells[0].ReadOnly=!can;row.Cells[0].ToolTipText=eligibility.Reason;row.Cells[2].ToolTipText=finding.Location;
            row.Cells[3].ToolTipText=finding.Reason+"\r\n"+eligibility.Reason;
            if(!can)row.DefaultCellStyle.ForeColor=Palette.Muted;
        }
        rendering=false;UpdateActions();
    }

    private void UpdateActions()
    {
        int count=results.Rows.Cast<DataGridViewRow>().Count(row=>!row.Cells[0].ReadOnly&&row.Cells[0].Value is true);
        selection.Text=showChecks.Checked?"只读明细":$"已选 {count} 项";
        selectAll.Enabled=!busy&&verified&&!showChecks.Checked&&results.Rows.Cast<DataGridViewRow>().Any(row=>!row.Cells[0].ReadOnly);
        clean.Enabled=!busy&&verified&&cleanup is not null&&count>0;
        rescan.Enabled=showChecks.Enabled=results.Enabled=!busy;
    }

    public Task RefreshForSelectionAsync()=>RefreshCoreAsync(false);
    private Task RefreshAsync()=>RefreshCoreAsync(true);
    private async Task RefreshCoreAsync(bool showError)
    {
        if(busy||closed)return;verified=false;Render();SetBusy(true);UpdateActions();summary.Text=current.Snapshot.SoftwareName+"\r\n正在检测…";
        try {var updated=await refreshResult(lifetime.Token);if(!closed){current=updated;verified=true;Render();}}
        catch(OperationCanceledException) { }
        catch(Exception ex){if(!closed){summary.Text=current.Snapshot.SoftwareName+"\r\n检测未完成，请重试";tips.SetToolTip(summary,ex.Message);if(showError)MessageBox.Show(this,ex.Message,"检测未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        finally {if(!closed){SetBusy(false);UpdateActions();}}
    }

    private async Task CleanAsync()
    {
        if(busy||cleanup is null||!verified)return;results.EndEdit();
        var selected=results.Rows.Cast<DataGridViewRow>().Where(row=>!row.Cells[0].ReadOnly&&row.Cells[0].Value is true&&row.Tag is ResidueFinding).Select(row=>(ResidueFinding)row.Tag!).ToArray();
        if(selected.Length==0)return;SetBusy(true);UpdateActions();
        try {
            summary.Text="正在核对所选项目…";var plan=await Task.Run(()=>cleanup.Prepare(current,selected));
            string locations=string.Join("\r\n",plan.Entries.Select(e=>e.Finding.Location));
            if(MessageBox.Show(this,$"删除所选 {plan.Entries.Count} 项？\r\n\r\n{locations}\r\n\r\n可能包含个人配置或共享数据。文件移入回收站，注册表先备份。","确认删除残留",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes){cleanup.Discard(plan);Render();return;}
            summary.Text="正在删除…";var outcome=await Task.Run(()=>cleanup.Execute(plan));
            current=await refreshResult(lifetime.Token);verified=true;Render();
            var failed=outcome.Outcomes.Where(item=>!item.Success).ToArray();
            if(failed.Length>0)MessageBox.Show(this,string.Join("\r\n",failed.Select(item=>item.Location+"\r\n"+item.Message)),"部分项目已保留",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }catch(Exception ex){if(!closed){Render();MessageBox.Show(this,ex.Message,"删除未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        finally{if(!closed){SetBusy(false);UpdateActions();}}
    }

    private static string SourceLabel(string source)=>source switch {
        "OriginalDirectory"=>"软件目录","OriginalMainExecutable"=>"主程序","OriginalRegistration"=>"卸载登记","SoftwareRegistry"=>"注册表",
        "AppData Local"=>"本地配置","AppData Roaming"=>"漫游配置","ProgramData"=>"公共数据",
        "AppData LocalRoot"=>"本地数据根","AppData RoamingRoot"=>"漫游数据根","ProgramDataRoot"=>"公共数据根",_=>source
    };
    private string? SelectedLocation=>results.CurrentRow?.Tag switch{ResidueFinding finding=>finding.Location,string path=>path,_=>null};
    private void CopyLocation(){try{if(SelectedLocation is string path)Clipboard.SetText(path);}catch(Exception ex){MessageBox.Show(this,ex.Message,"复制未完成");}}
    private void OpenLocation()
    {
        if(SelectedLocation is not string location)return;
        try {
            if(!Path.IsPathFullyQualified(location)||location.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException("此项为注册表位置，请使用“复制路径”。");
            string? directory=Directory.Exists(location)?location:File.Exists(location)?Path.GetDirectoryName(location):null;
            if(directory is null)throw new DirectoryNotFoundException("位置已不存在。");
            using var process=Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={directory}});
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"打开未完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    private void SetBusy(bool value){busy=value;if(!closed)BusyChanged?.Invoke(this,EventArgs.Empty);}
    protected override void Dispose(bool disposing)
    {
        if(disposing&&!disposed){disposed=true;closed=true;lifetime.Cancel();lifetime.Dispose();tips.Dispose();}
        base.Dispose(disposing);
    }
}
