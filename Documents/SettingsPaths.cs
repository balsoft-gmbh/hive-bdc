namespace Balsoft.Hive.BDC.Documents;

/// <summary>
/// Where a message sends the user: the Windows app's Settings window ("Settings > Outbound") or, on an Azure instance,
/// the web console's page of the same name ("Outbound"). Each text keeps its Windows words as the first argument, so an
/// installed customer reads exactly what it read before.
/// </summary>
public static class SettingsPaths
{
    /// <summary>Set once at start by the Azure run mode.</summary>
    public static bool Azure { get; set; }

    public static string Pick(string windows, string azure) => Azure ? azure : windows;
}