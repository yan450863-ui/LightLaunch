namespace LightLaunch;

public static class SoftwareIconService
{
    // Loading file resources never executes the target program.
    public static Bitmap Load(SoftwareItem item)
    {
        string? path = item.EntryPath ?? LibraryService.RecommendEntry(item.FolderPath, item.CandidateEntries);
        try
        {
            if (path is not null && LibraryService.IsSafeEntryPath(item.FolderPath, path))
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon.ToBitmap();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return SystemIcons.Application.ToBitmap();
    }
}
