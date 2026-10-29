using System.Security.Cryptography;
using System.Text;

namespace LightLaunch;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "数据"));
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--data-dir") dataDirectory = Path.GetFullPath(args[++i]);
        Directory.CreateDirectory(dataDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
        using var mutex = new Mutex(true, "Local\\LightLaunch_" + key, out bool first);
        if (!first) { MessageBox.Show("轻启已经在运行，请查看任务栏中的窗口。", "轻启"); return; }
        void Report(Exception exception)
        {
            try { File.AppendAllText(Path.Combine(dataDirectory, "error.log"), $"{DateTime.Now:O} {exception}\r\n"); } catch { }
            MessageBox.Show("操作未完成：" + exception.Message, "轻启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Application.ThreadException += (_, e) => Report(e.Exception);
        try { Application.Run(new MainForm(dataDirectory)); }
        catch (Exception ex) { Report(ex); }
    }
}
