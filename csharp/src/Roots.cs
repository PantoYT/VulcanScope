namespace VulcanScope;

/// <summary>Locates the VulcanScope project root (the directory holding credentials.json).</summary>
public static class Roots
{
    public static string FindRoot(string? explicitCreds)
    {
        if (!string.IsNullOrEmpty(explicitCreds))
            return Path.GetDirectoryName(Path.GetFullPath(explicitCreds))!;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "credentials.json")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static string CredentialsPath(string? explicitCreds) =>
        explicitCreds ?? Path.Combine(FindRoot(null), "credentials.json");

    public static string DataDir(string root) => Path.Combine(root, "data");
    public static string TemplatePath(string root) => Path.Combine(root, "web", "template.html");
    public static string DashboardPath(string root) => Path.Combine(root, "dashboard.html");
}
