using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>One Office table decides which files convert, whatever their variant.</summary>
public sealed class OfficeKindTableTests
{
    [Theory]
    [InlineData("report.docx", "Document")]
    [InlineData("letterhead.dotx", "Document")]
    [InlineData("macros.docm", "Document")]
    [InlineData("legacy.doc", "Document")]
    [InlineData("notes.rtf", "Document")]
    [InlineData("draft.odt", "Document")]
    [InlineData("deck.pptx", "Presentation")]
    [InlineData("kiosk.ppsx", "Presentation")]
    [InlineData("brand.potx", "Presentation")]
    [InlineData("old.pps", "Presentation")]
    [InlineData("talk.odp", "Presentation")]
    [InlineData("budget.xlsx", "Spreadsheet")]
    [InlineData("invoice.xltx", "Spreadsheet")]
    [InlineData("binary.xlsb", "Spreadsheet")]
    [InlineData("table.ods", "Spreadsheet")]
    public void TemplatesShowsAndMacroVariantsPreviewLikeTheirDocuments(string name, string kind)
    {
        Assert.Equal(Enum.Parse<OfficeKind>(kind), FileKinds.GetOfficeKind(name));
        Assert.False(FileKinds.CanUseSignatureRouting(name));
    }

    [Theory]
    [InlineData("graph.dot")]
    [InlineData("messages.pot")]
    [InlineData("notes.txt")]
    public void NamesOtherToolsOwnAreNotOffice(string name) =>
        Assert.Equal(OfficeKind.None, FileKinds.GetOfficeKind(name));

    [Theory]
    [InlineData("OfficeWord", "Document")]
    [InlineData("OfficePowerPoint", "Presentation")]
    [InlineData("OfficeExcel", "Spreadsheet")]
    [InlineData("Epub", "None")]
    public void OoxmlFoundByContentJoinsTheSameLane(string package, string kind) =>
        Assert.Equal(Enum.Parse<OfficeKind>(kind), FileKinds.GetOfficeKind(Enum.Parse<ZipPackageKind>(package)));
}
