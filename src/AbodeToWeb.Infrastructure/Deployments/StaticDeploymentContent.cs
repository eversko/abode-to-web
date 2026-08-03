namespace AbodeToWeb.Infrastructure.Deployments;

public static class StaticDeploymentContent
{
    public static bool ContainsServableFile(string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot)
                || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0) return false;

            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    var attributes = entry.Attributes;
                    // Static routes never serve through links or junctions. Ignore
                    // them when deciding whether an artifact has usable content.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry.FullName);
                    else return true;
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
