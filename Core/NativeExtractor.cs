using System.IO.Compression;

namespace ARC.Launcher.Core;

public static class NativeExtractor
{
    /// <summary>Only called for an explicit launch, into a fresh, caller-owned run directory.</summary>
    public static void Extract(MinecraftRoot root, IReadOnlyList<NativeArchive> archives, string destination)
    {
        root.ValidateContained(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("natives 输出目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(destination);
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            root.ValidateContained(archive.Path);
            using var zip = ZipFile.OpenRead(archive.Path);
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(p => p is ".." or "."))
                    throw new InvalidDataException("native ZIP 包含越界路径。");
                if (entry.Name.Length == 0 || archive.Excludes.Any(e => relative.StartsWith(e, StringComparison.Ordinal)) ||
                    relative.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length > 128 * 1024 * 1024 || (total += entry.Length) > 512 * 1024 * 1024)
                    throw new InvalidDataException("native ZIP 超过解压限制。");
                var target = root.ValidateContained(Path.Combine(destination, relative));
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (files.TryGetValue(target, out var previous))
                {
                    if (!previous.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("native DLL 冲突，拒绝覆盖。");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                root.ValidateContained(target);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes);
                files.Add(target, bytes);
            }
        }
    }
}
