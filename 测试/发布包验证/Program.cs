using System.Reflection;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Pass the extracted package root, then an optional screenshot path.");
        ApplicationConfiguration.Initialize();
        string package = Path.GetFullPath(args[0]);
        string program = Path.Combine(package, "程序");
        foreach (string file in new[] { "LightLaunch.exe", "LightLaunch.dll", "coreclr.dll", "hostfxr.dll", "System.Windows.Forms.dll" })
            if (!File.Exists(Path.Combine(program, file))) throw new Exception("Package file missing: " + file);
        if (Directory.Exists(Path.Combine(package, "数据"))) throw new Exception("Release must not contain user data.");
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(program, "LightLaunch.runtimeconfig.json")));
        if (runtime.RootElement.GetProperty("runtimeOptions").GetProperty("includedFrameworks").GetArrayLength() != 2)
            throw new Exception("Self-contained desktop runtime not declared.");

        // Run the actual packaged assembly's forms without invoking the elevated app host,
        // launching managed software, or reading the user's settings/registry records.
        var assembly = Assembly.LoadFrom(Path.Combine(program, "LightLaunch.dll"));
        if (!assembly.GetManifestResourceNames().Contains("LightLaunch.UserGuide.md")) throw new Exception("Embedded guide missing.");
        string temporary = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        int result = 0;
        try
        {
            var settingsType = assembly.GetType("LightLaunch.SettingsStore", true)!;
            var store = Activator.CreateInstance(settingsType, temporary)!;
            var settings = settingsType.GetProperty("Settings")!.GetValue(store)!;
            if (settings.GetType().GetProperty("LibraryRoot")!.GetValue(settings) is not null)
                throw new Exception("Fresh install must not preselect a developer library.");
            settingsType.GetMethod("Save")!.Invoke(store, null);

            using var form = (Form)Activator.CreateInstance(assembly.GetType("LightLaunch.MainForm", true)!, temporary)!;
            using var host = new PreviewHost { ClientSize = form.ClientSize, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(Screen.PrimaryScreen!.Bounds.Right + 100, 100) };
            form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None; form.MinimumSize = Size.Empty; form.Dock = DockStyle.Fill;
            host.Controls.Add(form); form.Show();
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, _) => {
                timer.Stop();
                try
                {
                    var grid = (DataGridView)form.GetType().GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                    if (grid.Rows.Count != 0) throw new Exception("Fresh library should be empty.");
                    using var guide = (Form)Activator.CreateInstance(assembly.GetType("LightLaunch.GuideDialog", true)!)!;
                    if (args.Length > 1)
                    {
                        string imagePath = Path.GetFullPath(args[1]);
                        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(imagePath);
                    }
                    Console.WriteLine("PASS packaged assembly, embedded guide, fresh settings, empty main form and self-contained runtime files.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
                finally { host.Close(); }
            };
            host.Shown += (_, _) => timer.Start();
            Application.Run(host);
        }
        finally
        {
            // Only remove the exact new fixture directory owned by this invocation.
            Directory.Delete(temporary, true);
        }
        return result;
    }

    private sealed class PreviewHost : Form { protected override bool ShowWithoutActivation => true; }
}
