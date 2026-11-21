using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Balsoft.Hive.BDC.App.ViewModels;
using Xunit;

namespace Balsoft.Hive.BDC.Tests;

/// <summary>
/// The Windows app as one window in the look of the web console (design 2026-11-19). The XAML only runs on Windows, so
/// these checks read the files: every function of the seven old windows has a place (W1), the theme owns every colour
/// and font (W6), one gold button per screen, no buttons in list rows but Retry and no dash pauses or AI tells (W7),
/// the fonts, logo and icons ship and every resource a view uses exists (W8), long lists stay virtualized (W5) and
/// every control has a name (W9). The guard before unsaved settings get lost (W2) is a unit test of ShellViewModel.
/// </summary>
public sealed class WindowsAppTests
{
    private static readonly string Repo = FindRepo();
    private static readonly string AppDir = Path.Combine(Repo, "app", "HiveBDC.App");
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string FindRepo()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "HiveBDC.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(AppDir, relative)).Replace("\r\n", "\n");

    /// <summary>The window and its pages, the files that show something.</summary>
    private static List<string> Views()
        => new[] { "MainWindow.xaml" }
            .Concat(Directory.GetFiles(Path.Combine(AppDir, "Pages"), "*.xaml").Select(f => Path.Combine("Pages", Path.GetFileName(f))))
            .ToList();

    private static string AllViews() => string.Concat(Views().Select(Read));

    private static string ViewsAndTheme() => AllViews() + Read(Path.Combine("Theme", "Controls.xaml"));

    private static List<string[]> Inventory(string kind)
    {
        string window = "";
        var rows = new List<string[]>();
        foreach (var line in File.ReadAllLines(Path.Combine(Repo, "tests", "Balsoft.Hive.BDC.Tests", "WindowsApp", "old-windows-inventory.txt")))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)) { window = line[3..].Trim(); continue; }
            var parts = line.Split('\t');
            if (parts[0] == kind) rows.Add(new[] { window }.Concat(parts.Skip(1)).ToArray());
        }
        Assert.NotEmpty(rows);
        return rows;
    }

    // W1: every function of the seven old windows has a place in the new one.

    [Fact]
    public void TheOldWindowsAreGoneAndOneWindowRemains()
    {
        foreach (var old in new[] { "SettingsWindow", "HistoryWindow", "DocumentsWindow", "LogsWindow", "WordCheckWindow", "ChangelogWindow" })
        {
            Assert.False(File.Exists(Path.Combine(AppDir, old + ".xaml")), old);
            Assert.False(File.Exists(Path.Combine(AppDir, old + ".xaml.cs")), old);
        }
        foreach (var page in new[] { "OverviewPage", "HistoryPage", "OutboundPage", "InboundPage", "ConnectionsPage", "ServicePage" })
            Assert.True(File.Exists(Path.Combine(AppDir, "Pages", page + ".xaml")), page);
        Assert.Single(Regex.Matches(string.Concat(Directory.GetFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).Select(File.ReadAllText)), "<Window\\b"));
    }

    [Fact]
    public void EveryCommandOfTheOldWindowsIsBoundInTheNewWindow()
    {
        string views = AllViews();
        foreach (var command in Inventory("command").Select(r => r[1]).Distinct())
            Assert.True(Regex.IsMatch(views, $@"Command=""\{{Binding (?:[\w.]*\.)?{command}\b"), $"{command} has no place in the new window");

        foreach (var row in Inventory("command").Where(r => r[1] == "OpenCommand"))
            Assert.Contains($"CommandParameter=\"{row[2]["param=".Length..]}\"", views);

        // The settings window's own menu (ShowPageCommand Outbound, Inbound, ...) is the sidebar now.
        string main = Read("MainWindow.xaml");
        string mainCode = Read("MainWindow.xaml.cs");
        foreach (var page in Inventory("command").Where(r => r[1] == "ShowPageCommand").Select(r => r[2]["param=".Length..]).Where(p => !p.StartsWith('{')))
            Assert.Matches($@"Tag=""{page}"" Click=""OnNav""", main);
        Assert.Contains("settings.ShowPageCommand.Execute(page)", mainCode);
    }

    /// <summary>Handlers of the old windows that have a new name or place, and the text that proves the place.</summary>
    private static readonly Dictionary<string, string> MovedHandlers = new()
    {
        ["MainWindow:SettingsMenuItem_Click"] = "AutomationProperties.AutomationId=\"NavOutbound\"",
        ["MainWindow:LogsMenuItem_Click"] = "AutomationProperties.AutomationId=\"TabLog\"",
        ["MainWindow:DocumentsMenuItem_Click"] = "AutomationProperties.AutomationId=\"TabDocuments\"",
        ["MainWindow:HistoryMenuItem_Click"] = "AutomationProperties.AutomationId=\"NavHistory\"",
        ["MainWindow:ChangelogMenuItem_Click"] = "Text=\"Release notes\"",
        ["MainWindow:AboutMenuItem_Click"] = "Text=\"About Hive BDC\"",
        ["MainWindow:RecentExport_DoubleClick"] = "MouseDoubleClick=\"OnRecentDoubleClick\"",
        ["DocumentsWindow:Document_DoubleClick"] = "MouseDoubleClick=\"OnDocumentDoubleClick\"",
        ["SettingsWindow:OnClose"] = "AutomationProperties.AutomationId=\"NavOverview\"",
        ["SettingsWindow:OnCloseWithoutSaving"] = "Click=\"OnLeaveWithoutSaving\"",
        ["SettingsWindow:OnRemoveTargetCard"] = "Click=\"OnRemoveDestination\"",
    };

    [Fact]
    public void EveryHandlerOfTheOldWindowsHasAPlace()
    {
        string views = AllViews();
        foreach (var row in Inventory("handler"))
        {
            string key = row[0] + ":" + row[1];
            string proof = MovedHandlers.TryGetValue(key, out var moved) ? moved : $"=\"{row[1]}\"";
            Assert.True(views.Contains(proof, StringComparison.Ordinal), $"{key} ({row[4]}) has no place: {proof}");
        }
    }

    [Fact]
    public void EveryListFieldAndNamedControlOfTheOldWindowsHasAPlace()
    {
        string views = ViewsAndTheme();
        foreach (var list in Inventory("list").Select(r => r[1]).Distinct())
            Assert.True(views.Contains($"ItemsSource=\"{{Binding {list}", StringComparison.Ordinal), $"list {list}");

        foreach (var input in Inventory("input").Select(r => (Path: r[1], Property: r[2].Split('.')[1])).Distinct())
            Assert.True(Regex.IsMatch(views, $@"\b{input.Property}=""\{{Binding {Regex.Escape(input.Path)}\b"), $"field {input.Path} ({input.Property})");

        var templateParts = new HashSet<string> { "b", "t", "Bd", "track", "thumb", "seg" };
        foreach (var name in Inventory("named").Select(r => r[1]).Distinct().Where(n => !templateParts.Contains(n)))
            Assert.True(views.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal), $"named control {name}");
    }

    [Fact]
    public void EveryAutomationIdOfTheOldWindowsStays()
    {
        var replaced = new Dictionary<string, string> { ["CloseSettings"] = "NavOverview", ["CloseWithoutSaving"] = "LeaveWithoutSaving" };
        string views = AllViews();
        foreach (var id in Inventory("automation").Select(r => r[1]).Distinct())
        {
            string expected = replaced.GetValueOrDefault(id, id);
            Assert.True(views.Contains($"AutomationProperties.AutomationId=\"{expected}\"", StringComparison.Ordinal), id);
        }
    }

    // W2: leaving the SET UP pages or closing with unsaved settings asks first.

    private sealed class Form
    {
        public bool Unsaved;
        public int Discarded;
    }

    private static (ShellViewModel Shell, Form Form) Shell()
    {
        var form = new Form();
        var shell = new ShellViewModel(() => form.Unsaved, "AnnaWeber", "PC-1", elevated: true);
        shell.DiscardSettings += () => form.Discarded++;
        return (shell, form);
    }

    [Fact]
    public void LeavingTheSetUpPagesWithUnsavedSettingsAsksFirst()
    {
        var (shell, form) = Shell();
        Assert.True(shell.Navigate(ShellViewModel.Outbound));
        form.Unsaved = true;

        Assert.False(shell.Navigate(ShellViewModel.Overview));
        Assert.Equal(ShellViewModel.Outbound, shell.Page);
        Assert.True(shell.ConfirmLeave);
        Assert.True(shell.ShowSaveBar);
        Assert.Equal("Leave without saving", shell.LeaveAction);
        Assert.Equal(0, form.Discarded);
    }

    [Fact]
    public void KeepEditingStaysOnThePageWithTheChanges()
    {
        var (shell, form) = Shell();
        shell.Navigate(ShellViewModel.Connections);
        form.Unsaved = true;
        shell.Navigate(ShellViewModel.History);

        shell.KeepEditing();

        Assert.Equal(ShellViewModel.Connections, shell.Page);
        Assert.False(shell.ConfirmLeave);
        Assert.Equal(0, form.Discarded);
    }

    [Fact]
    public void LeaveWithoutSavingDropsTheFormAndOpensThePage()
    {
        var (shell, form) = Shell();
        shell.Navigate(ShellViewModel.Inbound);
        form.Unsaved = true;
        shell.Navigate(ShellViewModel.History);

        Assert.False(shell.LeaveWithoutSaving());

        Assert.Equal(ShellViewModel.History, shell.Page);
        Assert.Equal(1, form.Discarded);
        Assert.False(shell.ConfirmLeave);
    }

    [Fact]
    public void MovingBetweenTheSetUpPagesKeepsTheOneFormWithoutAsking()
    {
        var (shell, form) = Shell();
        shell.Navigate(ShellViewModel.Outbound);
        form.Unsaved = true;

        foreach (var page in new[] { ShellViewModel.Inbound, ShellViewModel.Connections, ShellViewModel.Service, ShellViewModel.Outbound })
            Assert.True(shell.Navigate(page), page);
        Assert.False(shell.ConfirmLeave);
        Assert.Equal(0, form.Discarded);
    }

    [Fact]
    public void NothingUnsavedNeverAsks()
    {
        var (shell, form) = Shell();
        shell.Navigate(ShellViewModel.Outbound);
        Assert.True(shell.Navigate(ShellViewModel.Overview));
        Assert.True(shell.RequestClose());
        Assert.Equal(0, form.Discarded);
    }

    [Fact]
    public void ClosingWithUnsavedSettingsAsksAndCloseWithoutSavingCloses()
    {
        var (shell, form) = Shell();
        shell.Navigate(ShellViewModel.Service);
        shell.ServiceTab = "Licence";
        form.Unsaved = true;

        Assert.False(shell.RequestClose());
        Assert.True(shell.ShowSaveBar);
        Assert.Equal("Close without saving", shell.LeaveAction);
        Assert.Contains("Close without saving them?", shell.LeaveQuestion);

        Assert.True(shell.LeaveWithoutSaving());
        Assert.Equal(1, form.Discarded);
        Assert.True(shell.RequestClose());
    }

    [Fact]
    public void TheShellFollowsTheSettingsFormOnlyWithinTheSetUpPages()
    {
        var (shell, _) = Shell();
        shell.Follow(ShellViewModel.Connections);
        Assert.Equal(ShellViewModel.Overview, shell.Page);

        shell.Navigate(ShellViewModel.Outbound);
        shell.Follow(ShellViewModel.Connections);
        Assert.Equal(ShellViewModel.Connections, shell.Page);
    }

    [Fact]
    public void TheSaveBarShowsWhereSettingsAreEdited()
    {
        var (shell, _) = Shell();
        Assert.False(shell.ShowSaveBar);
        shell.Navigate(ShellViewModel.Service);
        Assert.True(shell.ShowSaveBar);
        shell.ServiceTab = "Log";
        Assert.False(shell.ShowSaveBar);
        Assert.Equal("Technical log", shell.ServiceTabTitle);
        shell.Navigate(ShellViewModel.History);
        Assert.False(shell.ShowSaveBar);
    }

    [Theory]
    [InlineData("AnnaWeber", "AW")]
    [InlineData("anna.weber", "AW")]
    [InlineData("CONTOSO\\AnnaWeber", "CA")]
    [InlineData("admin", "A")]
    [InlineData("", "?")]
    public void TheSidebarShowsTheInitialsOfTheWindowsUser(string user, string initials)
        => Assert.Equal(initials, ShellViewModel.InitialsOf(user));

    /// <summary>
    /// Each page gets its own data context, so its visibility must come from the window's shell; bound to the page's own
    /// context the Overview stayed visible under every other page (found on the Windows test machine, 2026-11-19).
    /// </summary>
    [Fact]
    public void OnlyThePageTheShellNamesIsVisible()
    {
        var pages = XDocument.Parse(Read("MainWindow.xaml")).Descendants().Where(e => e.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, pages.Count);
        foreach (var page in pages)
            Assert.Equal("{Binding DataContext.Page, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource Match}, ConverterParameter="
                         + page.Name.LocalName.Replace("Page", "") + "}",
                (string?)page.Attribute("Visibility"));
    }

    /// <summary>
    /// Two-way radios in a group write "false" into the record shown before while the page switches records; on
    /// the Windows test machine (2026-11-19) opening one company after another left the settings changed without an edit.
    /// </summary>
    [Fact]
    public void ChoicesWriteOnlyIntoTheRecordOnScreen()
    {
        var segments = Views().SelectMany(f => XDocument.Parse(Read(f)).Descendants()
            .Where(e => e.Name.LocalName == "RadioButton" && (string?)e.Attribute("Style") == "{StaticResource Segment}")).ToList();
        Assert.Equal(7, segments.Count);
        foreach (var radio in segments)
        {
            string property = (string)radio.Attribute("Tag")!;
            Assert.Equal($"{{Binding {property}, Mode=OneWay}}", (string?)radio.Attribute("IsChecked"));
            Assert.Equal("OnSegmentChecked", (string?)radio.Attribute("Checked"));
        }
        Assert.Contains("record.GetType().GetProperty(property)?.SetValue(record, true);", Read("Pages/OutboundPage.xaml.cs"));
    }

    [Fact]
    public void TheWindowAsksThroughTheShell()
    {
        string code = Read("MainWindow.xaml.cs");
        Assert.Contains("s.HasUnsavedChanges(ConnectionsView.PasswordTyped)", code);
        Assert.Contains("if (!_shell.RequestClose()) e.Cancel = true;", code);
        Assert.Contains("if (_shell.Navigate(page))", code);
        Assert.Contains("_shell.DiscardSettings += AttachSettings;", code);
    }

    // Technical log filter (design 2026-11-21): a Type select beside the legend, bound to the log view model.

    [Fact]
    public void TheTechnicalLogHasATypeSelectBesideItsLegend()
    {
        string xaml = Read("Pages/ServicePage.xaml");
        var doc = XDocument.Parse(xaml);
        var select = doc.Descendants().Single(e => e.Name.LocalName == "ComboBox" && (string?)e.Attribute("AutomationProperties.AutomationId") == "LogType");

        Assert.Equal("{Binding TypeChoices}", (string?)select.Attribute("ItemsSource"));
        Assert.Equal("{Binding LogType}", (string?)select.Attribute("SelectedValue"));
        Assert.Equal("Id", (string?)select.Attribute("SelectedValuePath"));
        Assert.Equal("Label", (string?)select.Attribute("DisplayMemberPath"));
        Assert.Equal("Type", (string?)select.Attribute("AutomationProperties.Name"));
        Assert.Contains(select.Ancestors(), a => (string?)a.Attribute(Xaml + "Name") == "LogTab");
        Assert.Contains("Text=\"Dashboard action\"", xaml);

        string vm = Read("ViewModels/LogsViewModel.cs");
        Assert.Contains("LogParser.Filter(", vm);
        Assert.Contains("LogTypes.Count(", vm);
        Assert.Contains("LogTypes.NoLines(", vm);
    }

    // W5: long lists stay virtualized.

    [Theory]
    [InlineData("Pages/HistoryPage.xaml", "StepsGrid")]
    [InlineData("Pages/HistoryPage.xaml", "DocumentsGrid")]
    [InlineData("Pages/OverviewPage.xaml", "RecentGrid")]
    [InlineData("Pages/ServicePage.xaml", "LogList")]
    public void LongListsAreVirtualizedAndNotInsideAScrollingPanel(string file, string name)
    {
        var doc = XDocument.Parse(Read(file));
        var list = doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
        Assert.DoesNotContain(list.Ancestors(), a => a.Name.LocalName is "ScrollViewer" or "StackPanel" or "WrapPanel" or "DockPanel");
        foreach (var attribute in new[] { "EnableRowVirtualization", "VirtualizingPanel.IsVirtualizing", "ScrollViewer.CanContentScroll" })
            Assert.NotEqual("False", (string?)list.Attribute(attribute));

        string controls = Read(Path.Combine("Theme", "Controls.xaml"));
        Assert.Contains("<Setter Property=\"EnableRowVirtualization\" Value=\"True\"/>", controls);
        Assert.Equal(2, Regex.Matches(controls, "<Setter Property=\"VirtualizingPanel.IsVirtualizing\" Value=\"True\"/>").Count);
    }

    // W6: colours and fonts live in the theme only.

    [Fact]
    public void NoColourOrFontIsSetOutsideTheTheme()
    {
        var brushAttributes = new HashSet<string> { "Foreground", "Background", "BorderBrush", "Fill", "Stroke", "Color", "CaretBrush", "SelectionBrush", "Effect", "FontFamily" };
        foreach (var file in Views().Append("App.xaml"))
        {
            var doc = XDocument.Parse(Read(file));
            foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()))
            {
                string name = attribute.Name.LocalName;
                if (brushAttributes.Contains(name) || name.EndsWith(".Foreground", StringComparison.Ordinal) || name.EndsWith(".Background", StringComparison.Ordinal)
                    || name.EndsWith(".FontFamily", StringComparison.Ordinal))
                    Assert.True(attribute.Value.StartsWith('{'), $"{file}: {name}=\"{attribute.Value}\" is set outside the theme");
                Assert.DoesNotMatch("#[0-9A-Fa-f]{3,8}\\b", attribute.Value);
            }
            Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush" or "Color" or "FontFamily" or "DropShadowEffect");
        }

        foreach (var code in Directory.GetFiles(Path.Combine(AppDir, "Converters"), "*.cs").Concat(Directory.GetFiles(Path.Combine(AppDir, "Pages"), "*.cs"))
                     .Append(Path.Combine(AppDir, "MainWindow.xaml.cs")))
        {
            string text = File.ReadAllText(code);
            Assert.DoesNotContain("Color.From", text);
            Assert.DoesNotContain("new SolidColorBrush", text);
            Assert.DoesNotMatch(@"Brushes\.(?!Transparent\b)\w+", text);
        }
    }

    // W7: one gold button per screen, no buttons in list rows but Retry, plain texts.

    [Fact]
    public void AtMostOneGoldButtonPerScreen()
    {
        foreach (var file in Views())
            Assert.True(Regex.Matches(Read(file), "Style=\"\\{StaticResource PrimaryButton\\}\"").Count <= 1, file);
    }

    [Fact]
    public void ListRowsCarryNoButtonsButRetry()
    {
        foreach (var file in Views())
            foreach (var grid in XDocument.Parse(Read(file)).Descendants().Where(e => e.Name.LocalName == "DataGrid"))
                foreach (var button in grid.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton" or "CheckBox"))
                    Assert.Equal("Retry", (string?)button.Attribute("AutomationProperties.AutomationId"));
    }

    private static readonly Regex AiTells = new(
        @"\b(delve|tapestry|testament|seamless(ly)?|leverage|holistic|streamline|comprehensive|robust|pivotal|crucial|vibrant|intricate|meticulous|showcase|underscore|garner|bolster(ed)?|boasts|enhance|fostering|landscape|valuable|certainly|great question|I hope this helps|don't hesitate|it's worth noting|let me know if)\b",
        RegexOptions.IgnoreCase);

    private static IEnumerable<(string Where, string Text)> Texts(string file)
    {
        var textAttributes = new HashSet<string> { "Text", "Content", "Header", "ToolTip", "Title", "AutomationProperties.Name" };
        foreach (var element in XDocument.Parse(Read(file)).Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => textAttributes.Contains(a.Name.LocalName)))
                if (!attribute.Value.StartsWith('{') || attribute.Value.StartsWith("{}", StringComparison.Ordinal))
                    yield return ($"{file} {element.Name.LocalName}.{attribute.Name.LocalName}", attribute.Value);
            if (element.Name.LocalName is "TextBlock" or "Run" && !element.HasElements && element.Value.Trim().Length > 0)
                yield return ($"{file} {element.Name.LocalName}", element.Value);
        }
    }

    [Fact]
    public void TextsHaveNoDashPausesAndNoAiTells()
    {
        var all = Views().SelectMany(Texts).ToList();
        Assert.True(all.Count > 300, $"only {all.Count} texts found");
        foreach (var (where, text) in all)
        {
            Assert.False(text.Contains('—') || text.Contains('–') || text.Contains(" - ", StringComparison.Ordinal), $"{where}: dash pause in \"{text}\"");
            Assert.False(AiTells.IsMatch(text), $"{where}: \"{text}\"");
        }

        var shell = Shell().Shell;
        shell.Navigate(ShellViewModel.Outbound);
        foreach (var text in new[] { shell.LeaveQuestion, shell.LeaveAction })
            Assert.DoesNotMatch("[—–]| - ", text);
    }

    [Fact]
    public void EachPageHasATitleAndAtMostOneLineUnderIt()
    {
        foreach (var file in Views().Where(f => f.StartsWith("Pages", StringComparison.Ordinal) && !f.Contains("Dialog", StringComparison.Ordinal)))
        {
            var doc = XDocument.Parse(Read(file));
            foreach (var title in doc.Descendants().Where(e => (string?)e.Attribute("Style") == "{StaticResource H1}"))
            {
                var heading = title.Parent!.Name.LocalName == "StackPanel" && (string?)title.Parent.Attribute("Orientation") == "Horizontal" ? title.Parent.Parent! : title.Parent!;
                int leads = heading.Elements().Count(e => (string?)e.Attribute("Style") == "{StaticResource Lead}");
                Assert.True(leads <= 1, $"{file}: {leads} lines under a title");
            }
        }
    }

    // W8: the fonts, the logo and the icons ship inside the app, and every resource a view names exists.

    private static readonly string[] Weights = ["Regular", "Medium", "SemiBold", "Bold"];
    private static readonly (string Family, string Stem, string Console)[] Fonts = [("DM Sans", "DMSans", "dm-sans"), ("Manrope", "Manrope", "manrope")];

    [Fact]
    public void TheConsoleFontsShipInsideTheAppWithTheirLicences()
    {
        string project = Read("HiveBDC.App.csproj");
        foreach (var (_, stem, console) in Fonts)
        {
            foreach (var weight in Weights)
                Assert.Contains($"<Resource Include=\"Assets\\Fonts\\{stem}-{weight}.ttf\" />", project);
            Assert.Contains($"<Content Include=\"Assets\\Fonts\\{console}-OFL.txt\" CopyToOutputDirectory=\"PreserveNewest\" />", project);
            Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(AppDir, "Assets", "Fonts", console + "-OFL.txt")));
        }
        Assert.Equal(Fonts.Length * Weights.Length, Regex.Matches(project, @"<Resource Include=""Assets\\Fonts\\[^""]+\.ttf"" />").Count);
    }

    // Design 2026-11-19-update-page-after-restart.md, amendment of 2026-11-20: Windows Server 2016 hangs in DirectWrite on
    // a variable font before the first window shows, so the app embeds static fonts only (F1), one per weight the theme
    // uses (F2), under the family names the theme asks for (F3), cut from the console's fonts at their version.

    [Fact]
    public void EveryEmbeddedFontIsStaticWithOneFilePerWeightAndTheConsolesVersion()
    {
        string folder = Path.Combine(AppDir, "Assets", "Fonts");
        Assert.Equal(Fonts.Length * Weights.Length, Directory.GetFiles(folder, "*.ttf").Length);
        foreach (var (family, stem, console) in Fonts)
        {
            string consoleVersion = NameRecords(Path.Combine(Repo, "web", "public", "fonts", console + ".ttf"))[5][0];
            var weights = new List<int>();
            foreach (var weight in Weights)
            {
                string path = Path.Combine(folder, $"{stem}-{weight}.ttf");
                var tables = FontTables(path);
                Assert.False(tables.ContainsKey("fvar") || tables.ContainsKey("gvar") || tables.ContainsKey("HVAR"), $"{stem}-{weight}.ttf is a variable font (F1)");
                byte[] d = File.ReadAllBytes(path);
                weights.Add((d[tables["OS/2"] + 4] << 8) | d[tables["OS/2"] + 5]);
                var names = NameRecords(path);
                Assert.Equal(family, names[16][0]);
                Assert.Equal(weight, names[17][0]);
                Assert.Equal(consoleVersion, names[5][0]);
            }
            Assert.Equal(new[] { 400, 500, 600, 700 }, weights);
        }

        string theme = Read(Path.Combine("Theme", "Theme.xaml"));
        var families = Regex.Matches(theme, @"/HiveBDC\.App;component/Assets/Fonts/#([^,<]+)").Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.Equal(Fonts.Select(f => f.Family), families);
    }

    [Fact]
    public void TheFontCheckFindsAVariableFont()
    {
        var tables = FontTables(Path.Combine(Repo, "web", "public", "fonts", "dm-sans.ttf"));
        Assert.True(tables.ContainsKey("fvar") && tables.ContainsKey("gvar"));
    }

    /// <summary>The table tags of a TrueType file with the offset of each table.</summary>
    private static Dictionary<string, int> FontTables(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        int count = (d[4] << 8) | d[5];
        var tables = new Dictionary<string, int>();
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + 16 * i;
            tables[Encoding.ASCII.GetString(d, entry, 4)] = (d[entry + 8] << 24) | (d[entry + 9] << 16) | (d[entry + 10] << 8) | d[entry + 11];
        }
        return tables;
    }

    /// <summary>The Windows-platform name records of a TrueType file, by name ID.</summary>
    private static Dictionary<int, List<string>> NameRecords(string path)
    {
        var records = new Dictionary<int, List<string>>();
        foreach (var (id, text) in RawNames(path))
        {
            if (!records.TryGetValue(id, out var list)) records[id] = list = new List<string>();
            list.Add(text);
        }
        return records;
    }

    private static IEnumerable<(int Id, string Text)> RawNames(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        int U16(int at) => (d[at] << 8) | d[at + 1];
        int U32(int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
        int tables = U16(4);
        for (int i = 0; i < tables; i++)
        {
            int entry = 12 + 16 * i;
            if (Encoding.ASCII.GetString(d, entry, 4) != "name") continue;
            int table = U32(entry + 8);
            int count = U16(table + 2), strings = table + U16(table + 4);
            for (int n = 0; n < count; n++)
            {
                int record = table + 6 + 12 * n;
                if (U16(record) == 3)
                    yield return (U16(record + 6), Encoding.BigEndianUnicode.GetString(d, strings + U16(record + 10), U16(record + 8)));
            }
        }
    }

    [Fact]
    public void TheLogoAndEveryIconAViewUsesAreDefined()
    {
        string icons = Read(Path.Combine("Theme", "Icons.xaml"));
        Assert.Contains("<Style x:Key=\"BrandLogo\" TargetType=\"Control\">", icons);
        Assert.Equal(17, Regex.Matches(icons, "<Path Data=\"M").Count);
        Assert.Contains("lucide 0.577.0 (ISC", icons);

        var defined = Regex.Matches(icons, "<Geometry x:Key=\"(Icon\\.\\w+)\">([^<]+)</Geometry>").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.True(defined.Count >= 20);
        foreach (var data in defined.Values)
            Assert.Matches(@"^M[\d\s.\-MmLlHhVvCcSsQqTtAaZz]+$", data);

        string used = ViewsAndTheme();
        foreach (Match m in Regex.Matches(used, @"\{StaticResource (Icon\.\w+)\}"))
            Assert.True(defined.ContainsKey(m.Groups[1].Value), m.Groups[1].Value);
        Assert.Contains("Style=\"{StaticResource BrandLogo}\"", Read("MainWindow.xaml"));
    }

    [Fact]
    public void EveryStaticResourceAViewNamesIsDefined()
    {
        string app = Read("App.xaml");
        foreach (var dictionary in new[] { "Theme/Theme.xaml", "Theme/Icons.xaml", "Theme/Controls.xaml" })
            Assert.Contains($"<ResourceDictionary Source=\"{dictionary}\"/>", app);

        static HashSet<string> Keys(string text) => Regex.Matches(text, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        static IEnumerable<string> Uses(string text) => Regex.Matches(text, @"\{StaticResource ([\w.]+)\}").Select(m => m.Groups[1].Value);

        var theme = Keys(Read(Path.Combine("Theme", "Theme.xaml")));
        var icons = Keys(Read(Path.Combine("Theme", "Icons.xaml")));
        var controls = Keys(Read(Path.Combine("Theme", "Controls.xaml")));
        var shared = theme.Concat(icons).Concat(controls).Concat(Keys(app)).ToHashSet();

        foreach (var key in Uses(Read(Path.Combine("Theme", "Icons.xaml"))))
            Assert.True(theme.Contains(key) || icons.Contains(key), $"Icons.xaml: {key}");
        foreach (var key in Uses(Read(Path.Combine("Theme", "Controls.xaml"))))
            Assert.True(theme.Contains(key) || icons.Contains(key) || controls.Contains(key), $"Controls.xaml: {key}");
        foreach (var file in Views())
        {
            string text = Read(file);
            var own = Keys(text);
            foreach (var key in Uses(text))
                Assert.True(shared.Contains(key) || own.Contains(key), $"{file}: {key}");
        }
    }

    /// <summary>
    /// A key defined twice lets the later dictionary win: a brush and a style both named NoticeError made a notice's
    /// Background a Style, and the window failed to open (found on the Windows test machine, 2026-11-19).
    /// </summary>
    [Fact]
    public void ResourceKeysAreUniqueAndOfTheRightKind()
    {
        var kinds = new Dictionary<string, string>();
        foreach (var file in new[] { "Theme/Theme.xaml", "Theme/Icons.xaml", "Theme/Controls.xaml", "App.xaml" })
            foreach (var element in XDocument.Parse(Read(file)).Descendants().Where(e => e.Attribute(Xaml + "Key") is not null))
            {
                string key = (string)element.Attribute(Xaml + "Key")!;
                Assert.True(kinds.TryAdd(key, element.Name.LocalName), $"{key} is defined twice ({file})");
            }

        var brushProperties = new HashSet<string> { "Background", "Foreground", "BorderBrush", "Fill", "Stroke", "CaretBrush", "SelectionBrush",
            "TextElement.Foreground", "HorizontalGridLinesBrush", "VerticalGridLinesBrush", "RowBackground" };
        foreach (var file in Views().Append("Theme/Controls.xaml").Append("Theme/Icons.xaml"))
        {
            var doc = XDocument.Parse(Read(file));
            var own = doc.Descendants().Where(e => e.Attribute(Xaml + "Key") is not null)
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Name.LocalName);
            string KindOf(string key) => own.TryGetValue(key, out var k) ? k : kinds.GetValueOrDefault(key, "missing");
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var m = Regex.Match(attribute.Value, @"^\{StaticResource ([\w.]+)\}$");
                    if (!m.Success) continue;
                    string key = m.Groups[1].Value;
                    string name = attribute.Name.LocalName;
                    if (name is "Style" or "BasedOn" or "FocusVisualStyle" or "ItemContainerStyle" or "ElementStyle")
                        Assert.True(KindOf(key) == "Style", $"{file}: {name} names {key}, a {KindOf(key)}");
                    if (brushProperties.Contains(name))
                        Assert.True(KindOf(key) == "SolidColorBrush", $"{file}: {name} names {key}, a {KindOf(key)}");
                    if (element.Name.LocalName == "Setter" && name == "Value" && brushProperties.Contains((string?)element.Attribute("Property") ?? ""))
                        Assert.True(KindOf(key) == "SolidColorBrush", $"{file}: a {element.Attribute("Property")!.Value} setter names {key}, a {KindOf(key)}");
                }
            }
        }
    }

    // W9: every control has a name a screen reader and an automation test can use.

    [Fact]
    public void EveryControlHasAnAccessibleName()
    {
        foreach (var file in Views())
        {
            foreach (var element in XDocument.Parse(Read(file)).Descendants())
            {
                string kind = element.Name.LocalName;
                bool named = element.Attribute("AutomationProperties.Name") is not null;
                if (kind is "TextBox" or "PasswordBox" or "ComboBox")
                    Assert.True(named, $"{file}: a {kind} without AutomationProperties.Name");
                if (kind is "Button" or "CheckBox" or "RadioButton" && element.Attribute("Content") is null)
                    Assert.True(named, $"{file}: a {kind} with no text of its own needs AutomationProperties.Name ({element.Attribute("Click")?.Value ?? element.Attribute("Command")?.Value})");
            }
        }

        string main = Read("MainWindow.xaml");
        foreach (var page in new[] { "Overview", "History", "Outbound", "Inbound", "Connections", "Service" })
            Assert.Contains($"AutomationProperties.AutomationId=\"Nav{page}\"", main);
    }

    /// <summary>Tab walked every cell of a list one by one before it reached anything after it (Windows test machine, 2026-11-19).</summary>
    [Fact]
    public void EveryReadOnlyListIsOneTabStop()
    {
        string controls = Read(Path.Combine("Theme", "Controls.xaml"));
        Assert.Matches(@"<Style TargetType=""DataGridCell"">\s*<Setter Property=""IsTabStop"" Value=""False""/>", controls);
        int lists = 0;
        foreach (var file in Views())
        {
            var grids = XDocument.Parse(Read(file)).Descendants()
                .Where(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute("Style") != "{StaticResource EditGrid}");
            foreach (var grid in grids)
            {
                string name = (string?)grid.Attribute(Xaml + "Name") ?? "";
                Assert.True(name.Length > 0, $"{file}: a read-only list without a name");
                Assert.Contains($"ListColumns.TabOnce({name});", Read(file + ".cs"));
                lists++;
            }
        }
        Assert.Equal(5, lists);
    }
}