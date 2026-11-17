using Azure.Data.Tables;
using Azure.Identity;
using Balsoft.Hive.BDC.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The state store contract against a real storage account. Each scenario gets its own tables (prefix Hbdc + 8 random
/// characters), which are deleted afterwards. Runs only with HIVEBDC_TABLES_CONNECTION_STRING, or with
/// HIVEBDC_TABLES_ENDPOINT and a sign-in that has Storage Table Data Contributor on the account (Azure CLI locally).
/// </summary>
public sealed class TableStateStoreLiveTests : IDisposable
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private readonly string _prefix = "Hbdc" + Guid.NewGuid().ToString("N")[..8];

    [TableLiveTheory]
    [MemberData(nameof(StateStoreScenarios.Names), MemberType = typeof(StateStoreScenarios))]
    public void ARealStorageAccountHolds(string scenario)
        => StateStoreScenarios.All[scenario](new TableStateStore(
            AzureTableRows.For(Env("HIVEBDC_TABLES_CONNECTION_STRING"), Env("HIVEBDC_TABLES_ENDPOINT")), NullLogger.Instance, _prefix));

    public void Dispose()
    {
        if (TableLiveTheoryAttribute.Missing) return;
        string connection = Env("HIVEBDC_TABLES_CONNECTION_STRING");
        var service = connection.Length > 0
            ? new TableServiceClient(connection)
            : new TableServiceClient(new Uri(Env("HIVEBDC_TABLES_ENDPOINT")), new DefaultAzureCredential());
        foreach (var table in new[] { "Exports", "Deliveries", "Events", "Documents", "Meta" })
            service.DeleteTable(_prefix + table);
    }
}

public sealed class TableLiveTheoryAttribute : TheoryAttribute
{
    public static bool Missing =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEBDC_TABLES_CONNECTION_STRING"))
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIVEBDC_TABLES_ENDPOINT"));

    public TableLiveTheoryAttribute()
    {
        if (Missing)
            Skip = "Set HIVEBDC_TABLES_CONNECTION_STRING, or HIVEBDC_TABLES_ENDPOINT with a sign-in that may write tables, to run this integration test.";
    }
}