using Balsoft.Hive.BDC.Configuration;
using Balsoft.Hive.BDC.Hosting;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public class WorkerSignatureTests
{
    private static BDCOptions Options(bool enabled)
    {
        var o = new BDCOptions();
        o.DocumentTypes.Add(new DocumentTypeOptions
        {
            Id = "PurchaseOrder", Enabled = enabled,
            Companies = { new DocumentRoute { DataAreaId = "agr", Input = @"C:\po",
                SharePoint = new SharePointTargetOptions { Enabled = true, SiteUrl = "https://x.sharepoint.com/sites/a", Library = "Docs" } } },
        });
        return o;
    }

    [Fact]
    public void TogglingADocumentTypeChangesTheSharePointSignature()
    {
        // The worker rebuilds or drops the shared uploader only when this signature changes, so
        // enabling or disabling a type with SharePoint on must move it.
        Assert.NotEqual(Worker.SharePointSignature(Options(true)), Worker.SharePointSignature(Options(false)));
        Assert.Equal(Worker.SharePointSignature(Options(true)), Worker.SharePointSignature(Options(true)));
    }
}
