using System.Text;

namespace Kamala.Application;

/// <summary>Builds safe download names from user-supplied file names (no paths, control or reserved characters).</summary>
public static class FileNaming
{
    private const int MaxStemLength = 80;

    public static string Stem(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Replace('\\', '/').Split('/')[^1]));
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' or '(' or ')' ? c : '_');
        var stem = builder.ToString().Trim(' ', '.');
        if (stem.Length > MaxStemLength)
            stem = stem[..MaxStemLength].Trim(' ', '.');
        return stem.Length == 0 ? "file" : stem;
    }

    /// <summary>Appends -2, -3... until the name is unused.</summary>
    public static string Unique(string name, ISet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}";
        return candidate;
    }
}
