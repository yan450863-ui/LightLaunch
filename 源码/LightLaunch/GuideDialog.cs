using System.Reflection;

namespace LightLaunch;

internal sealed class GuideDialog : Form
{
    public static void Open(IWin32Window owner){using var dialog=new GuideDialog();dialog.ShowDialog(owner);}
    public GuideDialog()
    {
        Palette.Style(this,"使用说明",new Size(800,650));FormBorderStyle=FormBorderStyle.Sizable;MinimumSize=new Size(640,480);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=1,RowCount=2};
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
        var content=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Palette.Surface,ForeColor=Palette.Ink,DetectUrls=false,ScrollBars=RichTextBoxScrollBars.Vertical};
        var card=new SurfacePanel{Dock=DockStyle.Fill,Padding=new Padding(20),Margin=Padding.Empty};card.Controls.Add(content);layout.Controls.Add(card,0,0);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,12,0,0),Margin=Padding.Empty};
        var close=Palette.Button("关闭",false,90);close.DialogResult=DialogResult.OK;buttons.Controls.Add(close);layout.Controls.Add(buttons,0,1);Controls.Add(layout);CancelButton=close;
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LightLaunch.UserGuide.md")??throw new InvalidDataException("使用说明未包含在程序中。");
        using var reader=new StreamReader(stream);
        foreach(string line in reader.ReadToEnd().Replace("\r","").Split('\n')) {
            bool heading=line.StartsWith('#');using var font=new Font(Font.FontFamily,heading?(line.StartsWith("# ")?18:12):10,heading?FontStyle.Bold:FontStyle.Regular);
            content.SelectionFont=font;content.AppendText((heading?line.TrimStart('#',' '):line)+"\n");
        }
        content.SelectionStart=0;Palette.FinishDialog(this);
    }
}
