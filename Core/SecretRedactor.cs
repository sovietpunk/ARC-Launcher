using System.Text.RegularExpressions;

namespace ARC.Launcher.Core;

public sealed class SecretRedactor(IEnumerable<string>? secrets = null)
{
    private readonly string[] values = (secrets ?? []).Where(s => !string.IsNullOrEmpty(s))
        .SelectMany(s => new[] { s, Uri.EscapeDataString(s) }).Distinct().OrderByDescending(s => s.Length).ToArray();
    public string Redact(string text)
    {
        foreach (var value in values) text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        return RedactPatterns(text);
    }
    public static string RedactPatterns(string text)
    {
        text = Regex.Replace(text, @"(?i)(Bearer\s+)[^\s\""']+", "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)((?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization)[\""']?\s*(?:[=:]|\s)\s*[\""']?)(?!\$\{)[^\s,\""']+",
            "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Regex.Replace(text, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", "[REDACTED]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
