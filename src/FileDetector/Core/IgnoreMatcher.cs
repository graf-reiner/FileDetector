using System.Text;
using System.Text.RegularExpressions;

namespace FileDetector.Core;

/// <summary>
/// Matches file/folder names against a list of shell-style glob patterns (<c>*</c>, <c>?</c>).
/// Used to suppress notifications for temp and partial-download files.
/// </summary>
public sealed class IgnoreMatcher
{
    /// <summary>Patterns applied when the user has not configured their own.</summary>
    public static readonly string[] DefaultPatterns =
    {
        "*.tmp",
        "*.temp",
        "*.crdownload",
        "*.part",
        "*.partial",
        "*.filepart",
        "~$*",
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    private readonly Regex[] _regexes;

    public IgnoreMatcher(IEnumerable<string>? patterns)
    {
        Patterns = (patterns ?? Array.Empty<string>())
            .Select(p => p?.Trim() ?? string.Empty)
            .Where(p => p.Length > 0)
            .ToArray();

        _regexes = Patterns.Select(ToRegex).ToArray();
    }

    public IReadOnlyList<string> Patterns { get; }

    public bool IsIgnored(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var re in _regexes)
        {
            if (re.IsMatch(name)) return true;
        }
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in glob)
        {
            switch (ch)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
