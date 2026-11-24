using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace UniDashboard.Core.Docx;

/// <summary>
/// Recreates the Word layout of the main project (MoodleAgent: Typst/Pandoc "academic" profile) as a placeholder template:
/// A4, 2.5 cm margins, Times New Roman 12 pt, centred title page (title 20 pt bold, course 13, author 12, Matrikelnummer 11, date 11),
/// header "Name · Matrikelnummer N" + date, centred page number. Placeholders: {{Title}} {{Course}} {{Author}} {{Matrikel}}
/// {{Date}} {{DueDate}} {{MoodleUrl}} {{Description}}.
/// </summary>
public static class TemplateBuilder
{
    const string Font = "Times New Roman";
    const int TextWidth = 11906 - 2 * 1417;

    public static void EnsureDefaultTemplate()
    {
        if (!File.Exists(Paths.DefaultTemplate)) Build(Paths.DefaultTemplate);
    }

    static RunProperties Rpr(int halfPts, bool bold = false)
    {
        var r = new RunProperties(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font });
        if (bold) r.Append(new Bold());
        r.Append(new FontSize { Val = halfPts.ToString() }, new FontSizeComplexScript { Val = halfPts.ToString() });
        return r;
    }

    static Paragraph Centered(string text, int halfPts, bool bold, int afterTwips, SectionProperties? sect = null)
    {
        var ppr = new ParagraphProperties(new SpacingBetweenLines { After = afterTwips.ToString() }, new Justification { Val = JustificationValues.Center });
        if (sect != null) ppr.Append(sect);
        return new Paragraph(ppr, new Run(Rpr(halfPts, bold), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    static Paragraph Body(string text, bool justify = true, int after = 200) =>
        new(new ParagraphProperties(new SpacingBetweenLines { After = after.ToString() },
                new Justification { Val = justify ? JustificationValues.Both : JustificationValues.Left }),
            new Run(Rpr(24), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    static PageMargin Margins() => new() { Top = 1417, Bottom = 1417, Left = 1417u, Right = 1417u, Header = 607u, Footer = 783u, Gutter = 0u };

    public static void Build(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new Header(new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 3, Space = 8, Color = "000000" }),
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = TextWidth }),
                new SpacingBetweenLines { After = "0" }),
            new Run(Rpr(19), new Text("{{Author}} · {{Matrikel}}") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(Rpr(19), new TabChar()),
            new Run(Rpr(19), new Text("{{Date}}"))));

        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new Footer(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(Rpr(24), new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(Rpr(24), new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(Rpr(24), new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(Rpr(24), new Text("1")),
            new Run(Rpr(24), new FieldChar { FieldCharType = FieldCharValues.End })));

        var titleSect = new SectionProperties(
            new PageSize { Width = 11906u, Height = 16838u }, Margins(), new VerticalTextAlignmentOnPage { Val = VerticalJustificationValues.Center });
        var bodySect = new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new PageSize { Width = 11906u, Height = 16838u }, Margins(),
            new PageNumberType { Start = 1 });

        var bodyEl = new Body(
            Centered("{{Title}}", 40, true, 480),
            Centered("{{Course}}", 26, false, 600),
            Centered("{{Author}}", 24, false, 120),
            Centered("{{Matrikel}}", 22, false, 300),
            Centered("{{Date}}", 22, false, 0, titleSect),
            Body("Abgabe: {{DueDate}}", false, 120),
            Body("{{MoodleUrl}}", false, 300),
            Body("{{Description}}"),
            bodySect);
        main.Document = new Document(bodyEl);

        // Document defaults: Times New Roman 12 pt
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font },
                new FontSize { Val = "24" }, new FontSizeComplexScript { Val = "24" },
                new Languages { Val = "de-DE" }))));
        main.Document.Save();
    }
}
