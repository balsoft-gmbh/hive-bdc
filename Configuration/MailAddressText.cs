namespace Balsoft.Hive.BDC.Configuration;

/// <summary>
/// Mail addresses as a person types them into one field (the Cc of a company): separated by ; , space, tab or new line.
/// Blanks are dropped and an address typed twice stays once, compared without case. Shared by the service and the
/// Windows app, so both read the same list from the same text; the web console mirrors it in mailCcList.
/// </summary>
public static class MailAddressText
{
    private static readonly char[] Separators = { ';', ',', ' ', '\t', '\r', '\n' };

    public static List<string> Parse(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string address = part.Trim();
            if (address.Length > 0 && seen.Add(address)) result.Add(address);
        }
        return result;
    }

    public static string Join(IEnumerable<string>? addresses) => string.Join("; ", addresses ?? Enumerable.Empty<string>());
}