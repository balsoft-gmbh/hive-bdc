using Balsoft.Hive.BDC.Documents.Word;
using PdfSharp.Drawing;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

public sealed class SystemFontResolverTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hivebdc-fonts-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    // The machine of an Azure instance: no Arial, Calibri or Cambria installed, only what the service brings.
    private static SystemFontResolver EmbeddedOnly() => SystemFontResolver.Create(Array.Empty<string>(), embedded: true);

    private static (string Family, int Style) FaceOf(SystemFontResolver fonts, PdfSharp.Fonts.FontResolverInfo info)
    {
        using var ms = new MemoryStream(fonts.GetFont(info.FaceName)!);
        Assert.True(SystemFontResolver.TryReadNames(ms, out var family, out int style));
        return (family, style);
    }

    public static TheoryData<string, string> Twins => new()
    {
        { "Arial", "Liberation Sans" }, { "Helvetica", "Liberation Sans" },
        { "Times New Roman", "Liberation Serif" }, { "Times", "Liberation Serif" },
        { "Courier New", "Liberation Mono" }, { "Courier", "Liberation Mono" },
        { "Calibri", "Carlito" }, { "Cambria", "Caladea" },
    };

    [Theory, MemberData(nameof(Twins))]
    public void OfficeFontThatIsNotInstalledIsDrawnWithItsMetricTwinInEveryStyle(string family, string twin)
    {
        var fonts = EmbeddedOnly();
        Assert.True(fonts.Has(family));
        Assert.Equal(twin, fonts.Draws(family));
        foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var info = fonts.ResolveTypeface(family, bold, italic)!;
            Assert.False(info.MustSimulateBold);
            Assert.False(info.MustSimulateItalic);
            Assert.Equal((twin, (bold ? 1 : 0) | (italic ? 2 : 0)), FaceOf(fonts, info));
        }
        Assert.Empty(fonts.MissingFamilies);
    }

    [Fact]
    public void FamilyWithoutATwinIsDrawnWithTheArialTwinAndReported()
    {
        var fonts = EmbeddedOnly();
        Assert.False(fonts.Has("Aptos"));
        Assert.Equal(("Liberation Sans", 1), FaceOf(fonts, fonts.ResolveTypeface("Aptos", true, false)!));
        Assert.Contains("Aptos", fonts.MissingFamilies);
    }

    [Fact]
    public void EveryFontTheServiceBringsLoads()
    {
        var fonts = EmbeddedOnly();
        Assert.Equal(20, fonts.FontCount);
        foreach (var family in new[] { "Liberation Sans", "Liberation Serif", "Liberation Mono", "Carlito", "Caladea" })
            Assert.Equal(family, fonts.Draws(family));
    }

    [Fact]
    public void InstalledFontWinsOverTheOneTheServiceBrings()
    {
        var embedded = EmbeddedOnly();
        Directory.CreateDirectory(_folder);
        string installed = Path.Combine(_folder, "installed-sans.ttf");
        File.WriteAllBytes(installed, embedded.GetFont(embedded.ResolveTypeface("Liberation Sans", false, false)!.FaceName)!);

        var fonts = SystemFontResolver.Create(new[] { _folder }, embedded: true);
        Assert.Equal(installed, fonts.ResolveTypeface("Liberation Sans", false, false)!.FaceName);
        Assert.Equal(installed, fonts.ResolveTypeface("Arial", false, false)!.FaceName);
        Assert.NotEqual(installed, fonts.ResolveTypeface("Arial", true, false)!.FaceName);   // bold only from the service
        Assert.Equal(20, fonts.FontCount);
    }

    // Design part 18c: Windows keeps Cambria Regular in cambria.ttc, so "Cambria" is installed with Bold, Italic and Bold
    // Italic only. Here Liberation Serif plays Cambria (bold face only) and Liberation Sans its twin.
    private string Installed(params (string Family, bool Bold, string File)[] faces)
    {
        var embedded = EmbeddedOnly();
        Directory.CreateDirectory(_folder);
        foreach (var (family, bold, file) in faces)
            File.WriteAllBytes(Path.Combine(_folder, file), embedded.GetFont(embedded.ResolveTypeface(family, bold, false)!.FaceName)!);
        return _folder;
    }

    private static readonly Dictionary<string, string> SerifTwin = new() { ["Liberation Serif"] = "Liberation Sans" };

    [Fact]
    public void AStyleTheInstalledFamilyLacksComesFromItsTwin()
    {
        string folder = Installed(("Liberation Serif", true, "serif-bold.ttf"), ("Liberation Sans", false, "sans-regular.ttf"));
        var fonts = SystemFontResolver.Create(new[] { folder }, embedded: false, SerifTwin);
        var regular = fonts.ResolveTypeface("Liberation Serif", false, false)!;
        Assert.Equal(Path.Combine(folder, "sans-regular.ttf"), regular.FaceName);
        Assert.False(regular.MustSimulateBold);
        Assert.Equal(Path.Combine(folder, "serif-bold.ttf"), fonts.ResolveTypeface("Liberation Serif", true, false)!.FaceName);
    }

    [Fact]
    public void WithoutATwinTheNearestStyleOfTheInstalledFamilyIsUsed()
    {
        string folder = Installed(("Liberation Serif", true, "serif-bold.ttf"), ("Liberation Sans", false, "sans-regular.ttf"));
        var fonts = SystemFontResolver.Create(new[] { folder }, embedded: false, new Dictionary<string, string>());
        Assert.Equal(Path.Combine(folder, "serif-bold.ttf"), fonts.ResolveTypeface("Liberation Serif", false, false)!.FaceName);
    }

    [Fact]
    public void AnInstalledStyleIsAlwaysItsOwnFile()
    {
        string folder = Installed(("Liberation Serif", false, "serif-regular.ttf"), ("Liberation Serif", true, "serif-bold.ttf"),
            ("Liberation Sans", false, "sans-regular.ttf"));
        var fonts = SystemFontResolver.Create(new[] { folder }, embedded: false, SerifTwin);
        Assert.Equal(Path.Combine(folder, "serif-regular.ttf"), fonts.ResolveTypeface("Liberation Serif", false, false)!.FaceName);
    }

    [InstalledFontFact("Arial")]
    public void ArialOnWindowsAndMacIsTheInstalledArial()
    {
        var fonts = SystemFontResolver.Install();
        Assert.Equal("Arial", fonts.Draws("Arial"));
        Assert.Equal(("Arial", 0), FaceOf(fonts, fonts.ResolveTypeface("Arial", false, false)!));
    }

    [Fact]
    public void WithoutAnyFontNothingResolves()
    {
        var fonts = SystemFontResolver.Create(Array.Empty<string>(), embedded: false);
        Assert.Equal(0, fonts.FontCount);
        Assert.Null(fonts.ResolveTypeface("Arial", false, false));
    }

    [InstalledFontFact("Arial")]
    public void LiberationSansMeasuresLikeArial() => SameWidths("Arial", "Liberation Sans");

    [InstalledFontFact("Times New Roman")]
    public void LiberationSerifMeasuresLikeTimesNewRoman() => SameWidths("Times New Roman", "Liberation Serif");

    [InstalledFontFact("Courier New")]
    public void LiberationMonoMeasuresLikeCourierNew() => SameWidths("Courier New", "Liberation Mono");

    [InstalledFontFact("Calibri")]
    public void CarlitoMeasuresLikeCalibri() => SameWidths("Calibri", "Carlito");

    [InstalledFontFact("Cambria")]
    public void CaladeaMeasuresLikeCambria() => SameWidths("Cambria", "Caladea");

    private static void SameWidths(string original, string twin)
    {
        SystemFontResolver.Install();
        var gfx = XGraphics.CreateMeasureContext(new XSize(595, 842), XGraphicsUnit.Point, XPageDirection.Downwards);
        const string text = "Abschlagsrechnung RE-2026-0042: Modulgebäude, 4 Raummodule, 2.000,00 € zzgl. 19 % USt (Größe ß)";
        foreach (var style in new[] { XFontStyleEx.Regular, XFontStyleEx.Bold, XFontStyleEx.Italic, XFontStyleEx.BoldItalic })
        {
            double expected = gfx.MeasureString(text, new XFont(original, 10, style)).Width;
            double actual = gfx.MeasureString(text, new XFont(twin, 10, style)).Width;
            // Within 0.05 %: Caladea has 1000 units per em, Cambria 2048, so each width is rounded (about 0.03 % on a line).
            Assert.True(Math.Abs(expected - actual) <= expected * 0.0005, $"{twin} {style}: {actual:0.000} pt, {original}: {expected:0.000} pt");
        }
    }
}

/// <summary>Runs only where the font is installed on the machine (Calibri and Cambria: Windows CI).</summary>
public sealed class InstalledFontFactAttribute : FactAttribute
{
    public InstalledFontFactAttribute(string family)
    {
        if (SystemFontResolver.Install().Draws(family) != family)
            Skip = $"{family} is not installed on this machine.";
    }
}