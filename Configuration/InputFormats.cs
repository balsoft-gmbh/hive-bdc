namespace Balsoft.Hive.BDC.Configuration;

/// <summary>Input formats of a company entry: ERP export pairs (JSON + PDF) or filled Word invoices. Shared with the dashboard.</summary>
public static class InputFormats
{
    public const string Json = "Json";
    public const string Word = "Word";

    public static bool IsWord(string? value) => string.Equals(value?.Trim(), Word, StringComparison.OrdinalIgnoreCase);
}