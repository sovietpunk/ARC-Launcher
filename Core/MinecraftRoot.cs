namespace ARC.Launcher.Core;

/// <summary>All game paths are resolved through this configured, read-only boundary.</summary>
public sealed class MinecraftRoot
{
    public string RootPath { get; }

    public MinecraftRoot(string rootPath)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        EnsureSafe(RootPath);
    }

    public string Resolve(params string[] components)
    {
        foreach (var component in components)
            if (string.IsNullOrWhiteSpace(component) || component is "." or ".." ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                component.Contains('/') || component.Contains('\\'))
                throw new ArgumentException("路径组件必须是单个合法名称。");
        var path = Path.GetFullPath(Path.Combine([RootPath, .. components]));
        if (!path.Equals(RootPath, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径超出配置的测试根目录。");
        EnsureSafe(path);
        return path;
    }

    public string ResolveRelative(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new ArgumentException("只接受测试根目录内的相对路径。");
        return Resolve(relativePath.Replace('\\', '/').Split('/'));
    }

    public string ValidateContained(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("游戏路径超出测试根目录。");
        EnsureSafe(fullPath);
        return fullPath;
    }

    // Check every existing ancestor, including ancestors of the configured root.
    public void EnsureSafe(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            try
            {
                // GetAttributes checks the link itself, including dangling links.
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"拒绝符号链接或目录联接：{current.FullName}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
