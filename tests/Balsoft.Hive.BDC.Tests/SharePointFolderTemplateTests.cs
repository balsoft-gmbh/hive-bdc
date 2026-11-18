using Balsoft.Hive.BDC.Documents;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>Failure case 75 (part 14f): a SharePoint folder built from the document stays inside the library.</summary>
public sealed class SharePointFolderTemplateTests
{
    private const string Invoice = """
        { "documentNumber": "RE-260042", "issueDate": "2026-11-18", "dataAreaId": "1000",
          "recipientName": "Muster/Handel ..GmbH", "dueDate": "", "evil": "../../Shared Documents", "odd": "a:b*c?" }
        """;

    [Theory]
    [InlineData("{IssueDate:yyyy}/{IssueDate:MM}", "2026/11")]
    [InlineData("Invoices/{dataAreaId}/{IssueDate:yyyy}", "Invoices/1000/2026")]
    [InlineData("{IssueDate:date}", "2026-11-18")]
    [InlineData("Fixed folder", "Fixed folder")]
    [InlineData("", "")]
    public void AFolderTemplateIsFilledFromTheDocument(string template, string expected)
        => Assert.Equal(expected, SharePointFieldTemplate.ResolvePath(Invoice, template));

    [Theory]
    [InlineData("{evil}", "-.-Shared Documents")]
    [InlineData("Customers/{recipientName}", "Customers/Muster-Handel .GmbH")]
    [InlineData("A/{odd}", "A/abc")]
    [InlineData("{dueDate:yyyy}/{documentNumber}", "RE-260042")]
    [InlineData("{missing}/x", "x")]
    public void AValueNeverLeavesTheLibraryOrBreaksTheFolder(string template, string expected)
        => Assert.Equal(expected, SharePointFieldTemplate.ResolvePath(Invoice, template));

    [Fact]
    public void ColumnsTakeTheSameDateFormats()
    {
        var map = new Dictionary<string, string> { ["Year"] = "{IssueDate:yyyy}", ["Date"] = "{IssueDate:date}", ["Number"] = "{DocumentNumber}" };
        var fields = SharePointFieldTemplate.Resolve(Invoice, map);
        Assert.Equal("2026", fields["Year"]);
        Assert.Equal("2026-11-18", fields["Date"]);
        Assert.Equal("RE-260042", fields["Number"]);
    }
}
