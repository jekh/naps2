using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Bounds = (int x, int y, int w, int h);

namespace NAPS2.Ocr;

internal static class HocrParser
{
    public static OcrResult Parse(XDocument hocrDocument)
    {
        var pageBounds = hocrDocument.Descendants()
            .Where(element => GetClass(element) == "ocr_page")
            .Select(GetBounds)
            .First();
        var words = new List<OcrResultElement>();
        var lines = new List<OcrResultElement>();
        foreach (var lineElement in hocrDocument.Descendants()
                     .Where(element => GetClass(element) is "ocr_line" or "ocr_header" or "ocr_textfloat"))
        {
            var lineBounds = GetBounds(lineElement);
            var lineAngle = GetTextAngle(lineElement);
            bool isRotated = lineAngle is >= 45 or <= -45;
            var baselineParams = GetBaselineParams(lineElement);
            var lineWords = lineElement.Descendants()
                .Where(element => GetClass(element) == "ocrx_word")
                .Where(element => !string.IsNullOrWhiteSpace(element.Value))
                .Select(wordElement =>
                {
                    var wordBounds = GetBounds(wordElement);
                    return new OcrResultElement(
                        wordElement.Value,
                        GetNearestAncestorAttribute(wordElement, "lang") ?? "",
                        GetNearestAncestorAttribute(wordElement, "dir") == "rtl",
                        wordBounds,
                        // TODO: Maybe we can properly handle rotated text?
                        isRotated
                            ? wordBounds.y + wordBounds.h
                            : CalculateBaseline(baselineParams, lineBounds, wordBounds),
                        GetFontSize(wordElement),
                        ImmutableList<OcrResultElement>.Empty);
                }).ToImmutableList();
            if (lineWords.Count == 0) continue;
            words.AddRange(lineWords);
            lines.Add(lineWords[0] with
            {
                Text = string.Join(" ", lineWords.Select(x => x.Text)),
                Bounds = lineBounds,
                Baseline = CalculateBaseline(baselineParams, lineBounds, lineBounds),
                Children = lineWords
            });
        }
        return new OcrResult(pageBounds, words.ToImmutableList(), lines.ToImmutableList());
    }

    private static string? GetNearestAncestorAttribute(XElement x, string attributeName)
    {
        var ancestor = x.AncestorsAndSelf().FirstOrDefault(x => x.Attribute(attributeName) != null);
        return ancestor?.Attribute(attributeName)?.Value;
    }

    private static string? GetClass(XElement? element)
    {
        return element?.Attribute("class")?.Value;
    }

    private static bool ParseData(XElement? element, string dataKey, int dataCount, out string[] parts)
    {
        parts = Array.Empty<string>();
        var titleAttr = element?.Attribute("title");
        if (titleAttr != null)
        {
            foreach (var param in titleAttr.Value.Split(';'))
            {
                parts = param.Trim().Split(' ');
                if (parts[0] == dataKey && parts.Length == dataCount + 1)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Bounds GetBounds(XElement? element)
    {
        var bounds = (0, 0, 0, 0);
        if (ParseData(element, "bbox", 4, out string[] parts))
        {
            int x1 = int.Parse(parts[1], CultureInfo.InvariantCulture),
                y1 = int.Parse(parts[2], CultureInfo.InvariantCulture);
            int x2 = int.Parse(parts[3], CultureInfo.InvariantCulture),
                y2 = int.Parse(parts[4], CultureInfo.InvariantCulture);
            bounds = (x1, y1, x2 - x1, y2 - y1);
        }
        return bounds;
    }

    private static int GetFontSize(XElement? element)
    {
        int fontSize = 0;
        if (ParseData(element, "x_fsize", 1, out string[] parts))
        {
            fontSize = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return fontSize;
    }

    private static (float m, float b) GetBaselineParams(XElement? element)
    {
        float m = 0;
        float b = 0;
        if (ParseData(element, "baseline", 2, out string[] parts))
        {
            m = float.Parse(parts[1], CultureInfo.InvariantCulture);
            b = float.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return (m, b);
    }

    private static float GetTextAngle(XElement? element)
    {
        float angle = 0;
        if (ParseData(element, "textangle", 1, out string[] parts))
        {
            angle = float.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return angle;
    }

    private static int CalculateBaseline((float m, float b) baselineParams, Bounds lineBounds, Bounds elementBounds)
    {
        // The line baseline is a linear equation (y=mx + b), so we calculate the word baseline from the
        // word offset to the left side of the line.
        float midpoint = elementBounds.x + elementBounds.w / 2f;
        int relativeBaseline = (int) Math.Round(baselineParams.b +
                                                baselineParams.m * (midpoint - lineBounds.x));
        int absoluteBaseline = relativeBaseline + lineBounds.y + lineBounds.h;
        return absoluteBaseline;
    }
}
