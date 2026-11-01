namespace LightLaunch;

internal static class LibraryRelocation
{
    public static void Recover(LauncherSettings settings, string launcherDirectory)
    {
        string? old = settings.LibraryRoot;
        if (string.IsNullOrWhiteSpace(old)) return;
        try
        {
            old = Path.GetFullPath(old);
            string? existingId = Directory.Exists(old) ? LibraryService.TryGetDirectoryId(old) : null;
            if (Directory.Exists(old) && (settings.LibraryRootIdentity is null || existingId == settings.LibraryRootIdentity))
            {
                settings.LibraryRootIdentity ??= existingId;
                return;
            }
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddChildren(string? parent)
            {
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
                foreach (string path in Directory.EnumerateDirectories(parent).Take(1000)) candidates.Add(Path.GetFullPath(path));
            }
            AddChildren(launcherDirectory);
            AddChildren(Path.GetDirectoryName(old));
            var matches = new List<string>();
            foreach (string candidate in candidates)
            {
                if (LibraryService.LibraryRootProblem(candidate) is not null || File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint)) continue;
                string? id = LibraryService.TryGetDirectoryId(candidate);
                if (settings.LibraryRootIdentity is not null)
                {
                    if (id == settings.LibraryRootIdentity) matches.Add(candidate);
                    continue;
                }
                int count = 0;
                foreach (var pair in settings.FolderIdentities)
                {
                    if (!pair.Value.StartsWith("fs", StringComparison.OrdinalIgnoreCase) || !LibraryService.IsPathInside(old, pair.Key)) continue;
                    string relative = Path.GetRelativePath(old, pair.Key);
                    string newPath = Path.GetFullPath(Path.Combine(candidate, relative));
                    if (LibraryService.TryGetDirectoryId(newPath) == pair.Value) count++;
                }
                if (count > 0) matches.Add(candidate);
            }
            if (matches.Count == 1)
            {
                settings.LibraryRoot = matches[0];
                settings.LibraryRootIdentity = LibraryService.TryGetDirectoryId(matches[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
