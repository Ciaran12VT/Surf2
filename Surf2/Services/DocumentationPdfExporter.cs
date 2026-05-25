using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Surf2.Services;

public sealed record DiagramDocumentationPdfSection(
    string Id,
    string Title,
    FlowDocument Document);

public sealed record DiagramDocumentationPdfLinkRegion(
    string SectionId,
    Rect ImageBounds);

public static class DocumentationPdfExporter
{
    private const double PageMargin = 54;
    private const double DiagramPageTitleHeight = 28;
    private const double ListIndent = 18;
    private const double ParagraphSpacing = 8;
    private const double MinimumFontSize = 8;
    private const double MaximumFontSize = 36;
    private const double MinimumDiagramLinkSize = 8;
    private static readonly Regex TextTokenPattern = new(@"\n|[^\S\n]+|[^\s]+", RegexOptions.Compiled);

    public static void Export(FlowDocument sourceDocument, string title, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sourceDocument);
        EnsureFontResolver();

        var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Creator = "Surf2";

        using var writer = new PdfDocumentationWriter(document);
        writer.WriteTitle(title);

        bool wroteContent = writer.WriteDocument(sourceDocument);

        if (!wroteContent)
        {
            writer.WriteParagraph(
                [new PdfTextSegment("No documentation.", TextStyle.Default)],
                TextAlignment.Left,
                0);
        }

        writer.Save(outputPath);
    }

    public static void ExportDiagramDocumentation(
        byte[] diagramPngBytes,
        int diagramPixelWidth,
        int diagramPixelHeight,
        IReadOnlyList<DiagramDocumentationPdfSection> sections,
        IReadOnlyList<DiagramDocumentationPdfLinkRegion> linkRegions,
        string title,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(diagramPngBytes);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(linkRegions);
        EnsureFontResolver();

        if (diagramPngBytes.Length == 0)
        {
            throw new ArgumentException("The diagram image is empty.", nameof(diagramPngBytes));
        }

        if (diagramPixelWidth <= 0 || diagramPixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diagramPixelWidth), "The diagram image dimensions must be positive.");
        }

        string normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Diagram Documentation" : title.Trim();
        var document = new PdfDocument();
        document.Info.Title = normalizedTitle;
        document.Info.Creator = "Surf2";

        PdfPage diagramPage = AddDiagramPage(
            document,
            diagramPngBytes,
            diagramPixelWidth,
            diagramPixelHeight,
            normalizedTitle,
            out XRect diagramImageRect);

        var destinations = new Dictionary<string, PdfDocumentationDestination>(StringComparer.OrdinalIgnoreCase);
        if (sections.Count > 0)
        {
            using var writer = new PdfDocumentationWriter(document);
            for (int i = 0; i < sections.Count; i++)
            {
                DiagramDocumentationPdfSection section = sections[i];
                if (i > 0)
                {
                    writer.StartNewPage();
                }

                destinations[section.Id] = writer.CreateDestination();
                writer.WriteTitle(section.Title);
                bool wroteContent = writer.WriteDocument(section.Document);
                if (!wroteContent)
                {
                    writer.WriteParagraph(
                        [new PdfTextSegment("No documentation.", TextStyle.Default)],
                        TextAlignment.Left,
                        0);
                }
            }
        }

        AddDiagramDocumentationLinks(
            diagramPage,
            diagramImageRect,
            diagramPixelWidth,
            diagramPixelHeight,
            linkRegions,
            destinations);

        document.Save(outputPath);
    }

    private static void EnsureFontResolver()
    {
        if (GlobalFontSettings.FontResolver == null)
        {
            GlobalFontSettings.FontResolver = DocumentationPdfFontResolver.Instance;
        }
    }

    private static PdfPage AddDiagramPage(
        PdfDocument document,
        byte[] diagramPngBytes,
        int diagramPixelWidth,
        int diagramPixelHeight,
        string title,
        out XRect imageRect)
    {
        PdfPage page = document.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = diagramPixelWidth >= diagramPixelHeight
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;

        using XGraphics graphics = XGraphics.FromPdfPage(page);
        double pageWidth = page.Width.Point;
        double pageHeight = page.Height.Point;
        var titleFont = new XFont(TextStyle.Default.FontFamily, 16, XFontStyleEx.Bold);
        graphics.DrawString(
            title,
            titleFont,
            XBrushes.Black,
            new XRect(PageMargin, PageMargin, pageWidth - (PageMargin * 2), DiagramPageTitleHeight),
            XStringFormats.TopLeft);

        double contentTop = PageMargin + DiagramPageTitleHeight;
        double contentWidth = Math.Max(1, pageWidth - (PageMargin * 2));
        double contentHeight = Math.Max(1, pageHeight - contentTop - PageMargin);
        double scale = Math.Min(contentWidth / diagramPixelWidth, contentHeight / diagramPixelHeight);
        double imageWidth = diagramPixelWidth * scale;
        double imageHeight = diagramPixelHeight * scale;
        imageRect = new XRect(
            PageMargin + ((contentWidth - imageWidth) / 2),
            contentTop + ((contentHeight - imageHeight) / 2),
            imageWidth,
            imageHeight);

        using var imageStream = new MemoryStream(diagramPngBytes);
        using XImage image = XImage.FromStream(imageStream);
        graphics.DrawImage(image, imageRect);
        return page;
    }

    private static void AddDiagramDocumentationLinks(
        PdfPage diagramPage,
        XRect diagramImageRect,
        int diagramPixelWidth,
        int diagramPixelHeight,
        IReadOnlyList<DiagramDocumentationPdfLinkRegion> linkRegions,
        IReadOnlyDictionary<string, PdfDocumentationDestination> destinations)
    {
        if (linkRegions.Count == 0 || destinations.Count == 0)
        {
            return;
        }

        double scaleX = diagramImageRect.Width / diagramPixelWidth;
        double scaleY = diagramImageRect.Height / diagramPixelHeight;
        var imageBounds = new Rect(0, 0, diagramPixelWidth, diagramPixelHeight);

        foreach (DiagramDocumentationPdfLinkRegion linkRegion in linkRegions)
        {
            if (!destinations.TryGetValue(linkRegion.SectionId, out PdfDocumentationDestination? destination))
            {
                continue;
            }

            Rect sourceBounds = linkRegion.ImageBounds;
            sourceBounds.Intersect(imageBounds);
            if (sourceBounds.IsEmpty)
            {
                continue;
            }

            double left = diagramImageRect.Left + (sourceBounds.Left * scaleX);
            double top = diagramImageRect.Top + (sourceBounds.Top * scaleY);
            double width = Math.Max(MinimumDiagramLinkSize, sourceBounds.Width * scaleX);
            double height = Math.Max(MinimumDiagramLinkSize, sourceBounds.Height * scaleY);
            var linkRect = new PdfRectangle(new XRect(left, top, width, height));
            diagramPage.AddDocumentLink(
                linkRect,
                destination.PageIndex,
                new XPoint(PageMargin, Math.Max(PageMargin, destination.Top - 4)));
        }
    }

    private sealed class PdfDocumentationWriter : IDisposable
    {
        private readonly PdfDocument _document;
        private PdfPage _page;
        private XGraphics _graphics;
        private int _pageIndex;
        private double _y;
        private double _pageWidth;
        private double _pageHeight;
        private bool _graphicsDisposed;
        private bool _disposed;

        public PdfDocumentationWriter(PdfDocument document)
        {
            _document = document;
            (_page, _graphics, _pageIndex) = AddPage();
            _pageWidth = _page.Width.Point;
            _pageHeight = _page.Height.Point;
            _y = PageMargin;
        }

        public void WriteTitle(string title)
        {
            string normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Documentation" : title.Trim();
            WriteParagraph(
                [new PdfTextSegment(normalizedTitle, TextStyle.Default with { FontSize = 18, IsBold = true })],
                TextAlignment.Left,
                0);
            _y += 4;
        }

        public bool WriteDocument(FlowDocument sourceDocument)
        {
            bool wroteContent = false;
            foreach (Block block in sourceDocument.Blocks)
            {
                wroteContent |= WriteBlock(block, 0);
            }

            return wroteContent;
        }

        public void StartNewPage()
        {
            DisposeGraphics();
            (_page, _graphics, _pageIndex) = AddPage();
            _graphicsDisposed = false;
            _pageWidth = _page.Width.Point;
            _pageHeight = _page.Height.Point;
            _y = PageMargin;
        }

        public PdfDocumentationDestination CreateDestination()
        {
            return new PdfDocumentationDestination(_pageIndex, _y);
        }

        public bool WriteBlock(Block block, int indentLevel)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    return WriteParagraph(paragraph, indentLevel, prefix: null);

                case Section section:
                    bool wroteSection = false;
                    foreach (Block childBlock in section.Blocks)
                    {
                        wroteSection |= WriteBlock(childBlock, indentLevel);
                    }

                    return wroteSection;

                case System.Windows.Documents.List list:
                    return WriteList(list, indentLevel);

                default:
                    string fallbackText = new TextRange(block.ContentStart, block.ContentEnd).Text.Trim();
                    if (string.IsNullOrWhiteSpace(fallbackText))
                    {
                        return false;
                    }

                    WriteParagraph(
                        [new PdfTextSegment(fallbackText, TextStyle.Default)],
                        TextAlignment.Left,
                        indentLevel);
                    return true;
            }
        }

        public void WriteParagraph(IReadOnlyList<PdfTextSegment> segments, TextAlignment alignment, int indentLevel)
        {
            RenderParagraph(segments, alignment, indentLevel);
        }

        public void Save(string outputPath)
        {
            DisposeGraphics();
            _document.Save(outputPath);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            DisposeGraphics();
            _disposed = true;
        }

        private bool WriteParagraph(Paragraph paragraph, int indentLevel, string? prefix)
        {
            TextStyle paragraphStyle = ExtractTextStyle(paragraph, TextStyle.Default);
            var segments = new List<PdfTextSegment>();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                segments.Add(new PdfTextSegment(prefix, paragraphStyle));
            }

            foreach (Inline inline in paragraph.Inlines)
            {
                AddInlineSegments(inline, paragraphStyle, segments);
            }

            if (!segments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text)))
            {
                return false;
            }

            double topSpacing = GetFiniteSpacing(paragraph.Margin.Top, 0);
            if (topSpacing > 0)
            {
                _y += Math.Min(topSpacing, ParagraphSpacing);
            }

            RenderParagraph(segments, paragraph.TextAlignment, indentLevel);

            double bottomSpacing = Math.Max(ParagraphSpacing, GetFiniteSpacing(paragraph.Margin.Bottom, 0));
            _y += Math.Min(bottomSpacing, ParagraphSpacing * 1.5);
            return true;
        }

        private bool WriteList(System.Windows.Documents.List list, int indentLevel)
        {
            bool wroteAny = false;
            int itemNumber = list.StartIndex <= 0 ? 1 : list.StartIndex;
            foreach (ListItem item in list.ListItems)
            {
                bool wroteItem = false;
                string marker = GetListMarker(list.MarkerStyle, itemNumber);
                foreach (Block itemBlock in item.Blocks)
                {
                    if (itemBlock is Paragraph paragraph)
                    {
                        wroteItem |= WriteParagraph(paragraph, indentLevel + 1, wroteItem ? null : marker);
                    }
                    else
                    {
                        wroteItem |= WriteBlock(itemBlock, indentLevel + 1);
                    }
                }

                wroteAny |= wroteItem;
                itemNumber++;
            }

            return wroteAny;
        }

        private void RenderParagraph(IReadOnlyList<PdfTextSegment> segments, TextAlignment alignment, int indentLevel)
        {
            double left = PageMargin + (indentLevel * ListIndent);
            double maxWidth = Math.Max(96, _pageWidth - PageMargin - left);
            foreach (PdfLine line in WrapSegments(segments, maxWidth))
            {
                EnsureSpace(line.Height);
                double lineX = GetAlignedX(left, maxWidth, line.Width, alignment);
                double x = lineX;
                foreach (PdfTextSegment segment in line.Segments)
                {
                    XFont font = CreateFont(segment.Style);
                    XBrush brush = CreateBrush(segment.Style.Color);
                    double width = Measure(segment.Text, font);
                    _graphics.DrawString(
                        segment.Text,
                        font,
                        brush,
                        new XRect(x, _y, width, line.Height),
                        XStringFormats.TopLeft);

                    DrawTextDecorations(segment.Style, x, _y, width, font);
                    x += width;
                }

                _y += line.Height;
            }
        }

        private IEnumerable<PdfLine> WrapSegments(IReadOnlyList<PdfTextSegment> segments, double maxWidth)
        {
            var currentLine = new PdfLine();

            foreach (PdfTextSegment segment in segments)
            {
                foreach (string token in Tokenize(segment.Text))
                {
                    if (token == "\n")
                    {
                        yield return currentLine.HasContent ? currentLine : PdfLine.Empty(segment.Style);
                        currentLine = new PdfLine();
                        continue;
                    }

                    if (!currentLine.HasContent && string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    XFont font = CreateFont(segment.Style);
                    double tokenWidth = Measure(token, font);
                    if (currentLine.HasContent && currentLine.Width + tokenWidth > maxWidth)
                    {
                        yield return currentLine;
                        currentLine = new PdfLine();
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            continue;
                        }
                    }

                    if (tokenWidth > maxWidth && !currentLine.HasContent)
                    {
                        foreach (string tokenPart in SplitOversizedToken(token, segment.Style, maxWidth))
                        {
                            currentLine.Add(new PdfTextSegment(tokenPart, segment.Style), Measure(tokenPart, font));
                            yield return currentLine;
                            currentLine = new PdfLine();
                        }

                        continue;
                    }

                    currentLine.Add(new PdfTextSegment(token, segment.Style), tokenWidth);
                }
            }

            if (currentLine.HasContent)
            {
                yield return currentLine;
            }
        }

        private IEnumerable<string> SplitOversizedToken(string token, TextStyle style, double maxWidth)
        {
            if (string.IsNullOrEmpty(token))
            {
                yield break;
            }

            XFont font = CreateFont(style);
            var current = string.Empty;
            foreach (char character in token)
            {
                string candidate = current + character;
                if (current.Length > 0 && Measure(candidate, font) > maxWidth)
                {
                    yield return current;
                    current = character.ToString();
                    continue;
                }

                current = candidate;
            }

            if (current.Length > 0)
            {
                yield return current;
            }
        }

        private void EnsureSpace(double requiredHeight)
        {
            if (_y + requiredHeight <= _pageHeight - PageMargin)
            {
                return;
            }

            StartNewPage();
        }

        private (PdfPage Page, XGraphics Graphics, int PageIndex) AddPage()
        {
            PdfPage page = _document.AddPage();
            page.Size = PageSize.A4;
            XGraphics graphics = XGraphics.FromPdfPage(page);
            return (page, graphics, _document.Pages.Count - 1);
        }

        private void DisposeGraphics()
        {
            if (_graphicsDisposed)
            {
                return;
            }

            _graphics.Dispose();
            _graphicsDisposed = true;
        }

        private double Measure(string text, XFont font)
        {
            return _graphics.MeasureString(text, font).Width;
        }

        private void DrawTextDecorations(TextStyle style, double x, double y, double width, XFont font)
        {
            if (!style.IsUnderline && !style.IsStrikethrough)
            {
                return;
            }

            var pen = new XPen(style.Color, Math.Max(0.5, font.Size / 14));
            if (style.IsUnderline)
            {
                double underlineY = y + (font.Size * 1.05);
                _graphics.DrawLine(pen, x, underlineY, x + width, underlineY);
            }

            if (style.IsStrikethrough)
            {
                double strikeY = y + (font.Size * 0.58);
                _graphics.DrawLine(pen, x, strikeY, x + width, strikeY);
            }
        }

        private static double GetAlignedX(double left, double maxWidth, double lineWidth, TextAlignment alignment)
        {
            return alignment switch
            {
                TextAlignment.Center => left + Math.Max(0, (maxWidth - lineWidth) / 2),
                TextAlignment.Right => left + Math.Max(0, maxWidth - lineWidth),
                _ => left
            };
        }

        private static XFont CreateFont(TextStyle style)
        {
            string family = string.IsNullOrWhiteSpace(style.FontFamily)
                ? TextStyle.Default.FontFamily
                : style.FontFamily;

            XFontStyleEx fontStyle = style switch
            {
                { IsBold: true, IsItalic: true } => XFontStyleEx.BoldItalic,
                { IsBold: true } => XFontStyleEx.Bold,
                { IsItalic: true } => XFontStyleEx.Italic,
                _ => XFontStyleEx.Regular
            };

            try
            {
                return new XFont(family, style.FontSize, fontStyle);
            }
            catch
            {
                return new XFont(TextStyle.Default.FontFamily, style.FontSize, fontStyle);
            }
        }
    }

    private sealed record PdfDocumentationDestination(int PageIndex, double Top);

    private sealed class PdfLine
    {
        private const double LineHeightMultiplier = 1.35;

        public List<PdfTextSegment> Segments { get; } = [];

        public double Width { get; private set; }

        public double Height { get; private set; }

        public bool HasContent => Segments.Count > 0;

        public static PdfLine Empty(TextStyle style)
        {
            var line = new PdfLine();
            line.Height = style.FontSize * LineHeightMultiplier;
            return line;
        }

        public void Add(PdfTextSegment segment, double width)
        {
            Segments.Add(segment);
            Width += width;
            Height = Math.Max(Height, segment.Style.FontSize * LineHeightMultiplier);
        }
    }

    private sealed record PdfTextSegment(string Text, TextStyle Style);

    private sealed record TextStyle(
        string FontFamily,
        double FontSize,
        bool IsBold,
        bool IsItalic,
        bool IsUnderline,
        bool IsStrikethrough,
        XColor Color)
    {
        public static TextStyle Default { get; } = new(
            "Segoe UI",
            12,
            false,
            false,
            false,
            false,
            XColors.Black);
    }

    private static TextStyle ExtractTextStyle(TextElement element, TextStyle inherited)
    {
        string fontFamily = inherited.FontFamily;
        double fontSize = inherited.FontSize;
        bool isBold = inherited.IsBold;
        bool isItalic = inherited.IsItalic;
        bool isUnderline = inherited.IsUnderline;
        bool isStrikethrough = inherited.IsStrikethrough;
        XColor color = inherited.Color;

        if (element.ReadLocalValue(TextElement.FontFamilyProperty) != DependencyProperty.UnsetValue)
        {
            fontFamily = element.FontFamily.Source;
        }

        if (element.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue &&
            double.IsFinite(element.FontSize))
        {
            fontSize = Math.Clamp(element.FontSize, MinimumFontSize, MaximumFontSize);
        }

        if (element.ReadLocalValue(TextElement.FontWeightProperty) != DependencyProperty.UnsetValue)
        {
            isBold = element.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
        }

        if (element.ReadLocalValue(TextElement.FontStyleProperty) != DependencyProperty.UnsetValue)
        {
            isItalic = element.FontStyle == FontStyles.Italic || element.FontStyle == FontStyles.Oblique;
        }

        if (element.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue)
        {
            color = ToXColor(element.Foreground);
        }

        if (element is Inline inline &&
            inline.ReadLocalValue(Inline.TextDecorationsProperty) != DependencyProperty.UnsetValue)
        {
            TextDecorationCollection decorations = inline.TextDecorations;
            isUnderline = HasDecoration(decorations, TextDecorationLocation.Underline);
            isStrikethrough = HasDecoration(decorations, TextDecorationLocation.Strikethrough);
        }

        return new TextStyle(fontFamily, fontSize, isBold, isItalic, isUnderline, isStrikethrough, color);
    }

    private static void AddInlineSegments(Inline inline, TextStyle inherited, ICollection<PdfTextSegment> segments)
    {
        TextStyle style = ExtractTextStyle(inline, inherited);
        switch (inline)
        {
            case Run run:
                if (!string.IsNullOrEmpty(run.Text))
                {
                    segments.Add(new PdfTextSegment(NormalizeLineBreaks(run.Text), style));
                }

                break;

            case LineBreak:
                segments.Add(new PdfTextSegment("\n", style));
                break;

            case Span span:
                foreach (Inline childInline in span.Inlines)
                {
                    AddInlineSegments(childInline, style, segments);
                }

                break;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TextTokenPattern.Matches(NormalizeLineBreaks(text)))
        {
            string token = match.Value;
            yield return string.IsNullOrWhiteSpace(token) && token != "\n"
                ? " "
                : token;
        }
    }

    private static string NormalizeLineBreaks(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static double GetFiniteSpacing(double value, double fallback)
    {
        return double.IsFinite(value) ? Math.Max(0, value) : fallback;
    }

    private static bool HasDecoration(TextDecorationCollection decorations, TextDecorationLocation location)
    {
        return decorations.Any(decoration => decoration.Location == location);
    }

    private static string GetListMarker(TextMarkerStyle markerStyle, int itemNumber)
    {
        return markerStyle switch
        {
            TextMarkerStyle.Decimal => $"{itemNumber}. ",
            TextMarkerStyle.LowerLatin => $"{ToAlphabeticListMarker(itemNumber, uppercase: false)}. ",
            TextMarkerStyle.UpperLatin => $"{ToAlphabeticListMarker(itemNumber, uppercase: true)}. ",
            _ => "* "
        };
    }

    private static string ToAlphabeticListMarker(int itemNumber, bool uppercase)
    {
        int index = Math.Max(1, itemNumber);
        var chars = new Stack<char>();
        while (index > 0)
        {
            index--;
            chars.Push((char)((uppercase ? 'A' : 'a') + (index % 26)));
            index /= 26;
        }

        return new string(chars.ToArray());
    }

    private static XBrush CreateBrush(XColor color)
    {
        return new XSolidBrush(color);
    }

    private static XColor ToXColor(WpfBrush brush)
    {
        if (brush is WpfSolidColorBrush solidColorBrush)
        {
            Color color = solidColorBrush.Color;
            return XColor.FromArgb(color.A, color.R, color.G, color.B);
        }

        return XColors.Black;
    }
}
