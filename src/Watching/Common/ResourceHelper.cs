using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Watching.Common;

/// <summary>读取内嵌的网页资源（手机客户端 / 电脑网页客户端）。</summary>
public static class ResourceHelper
{
    /// <summary>按逻辑路径读取，例如 "web/mobile/index.html"。</summary>
    public static byte[] Read(string logicalPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        string slug = logicalPath.Replace('/', '.').Replace('\\', '.');
        string full = asm.GetName().Name + "." + slug;

        using var s = asm.GetManifestResourceStream(full);
        if (s == null)
        {
            // 退化：按后缀匹配
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(slug, StringComparison.OrdinalIgnoreCase))
                {
                    using var s2 = asm.GetManifestResourceStream(name);
                    return ReadAll(s2);
                }
            }
            return null;
        }
        return ReadAll(s);
    }

    public static string ReadText(string logicalPath)
    {
        var bytes = Read(logicalPath);
        return bytes == null ? null : new UTF8Encoding(false).GetString(bytes);
    }

    private static byte[] ReadAll(Stream s)
    {
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
