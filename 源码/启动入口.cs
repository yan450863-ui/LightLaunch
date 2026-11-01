using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static void Main()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "程序");
        string executable = Path.Combine(directory, "LightLaunch.exe");
        try
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到轻启主程序，请保持程序文件夹与启动入口放在一起。", executable);
            Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "轻启", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
