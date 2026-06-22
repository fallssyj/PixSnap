using System.Globalization;

namespace PixSnap.Services.OcrLayout;

/// <summary>段内自然段分析，预测行尾分隔符。参考 Umi-OCR paragraph_parse。</summary>
internal static class OcrParagraphParser
{
    private const double LineHeightThreshold = 1.2;

    public static void Run(List<OcrLayoutBlock> blocks)
    {
        if (blocks.Count == 0)
            return;

        var units = blocks
            .Select(b => new ParaUnit(b.NormalizedBBox, (FirstChar(b.Text), LastChar(b.Text)), b))
            .ToList();

        units.Sort((a, b) => a.Bbox.Y0.CompareTo(b.Bbox.Y0));

        var (paraL, _, paraR, paraBottom) = units[0].Bbox;
        double paraLineH = paraBottom - units[0].Bbox.Y0;
        double? paraLineS = null;
        var nowPara = new List<ParaUnit> { units[0] };
        var paras = new List<List<ParaUnit>>();
        var parasLineSpace = new List<double?>();

        for (int i = 1; i < units.Count; i++)
        {
            var (l, top, r, bottom) = units[i].Bbox;
            double h = bottom - top;
            double ls = top - paraBottom;
            bool samePara = Math.Abs(paraL - l) <= paraLineH * LineHeightThreshold
                            && Math.Abs(paraR - r) <= paraLineH * LineHeightThreshold
                            && (paraLineS is null || ls < paraLineS + paraLineH * 0.5);

            if (samePara)
            {
                paraL = (paraL + l) / 2;
                paraR = (paraR + r) / 2;
                paraLineH = (paraLineH + h) / 2;
                paraLineS = paraLineS is null ? ls : (paraLineS.Value + ls) / 2;
                nowPara.Add(units[i]);
            }
            else
            {
                paras.Add(nowPara);
                parasLineSpace.Add(paraLineS);
                nowPara = [units[i]];
                paraL = l;
                paraR = r;
                paraLineH = h;
                paraLineS = null;
            }

            paraBottom = bottom;
        }

        paras.Add(nowPara);
        parasLineSpace.Add(paraLineS);

        for (int i1 = paras.Count - 1; i1 >= 0; i1--)
        {
            var para = paras[i1];
            if (para.Count != 1)
                continue;

            var (l, top, r, bottom) = para[0].Bbox;
            bool upFlag = false;
            bool downFlag = false;

            if (i1 > 0)
            {
                var (upL, upTop, upR, upBottom) = paras[i1 - 1][^1].Bbox;
                double upH = upBottom - upTop;
                upFlag = Math.Abs(upL - l) <= upH * LineHeightThreshold && r <= upR + upH * LineHeightThreshold;
                if (parasLineSpace[i1 - 1] is double upLs && top - upBottom > upLs + upH * 0.5)
                    upFlag = false;
            }

            if (i1 < paras.Count - 1)
            {
                var (downL, downTop, downR, downBottom) = paras[i1 + 1][0].Bbox;
                double downH = downBottom - downTop;
                if (downL - downH * LineHeightThreshold <= l && l <= downL + downH * (1 + LineHeightThreshold))
                {
                    downFlag = paras[i1 + 1].Count > 1
                        ? Math.Abs(downR - r) <= downH * LineHeightThreshold
                        : downR - downH * LineHeightThreshold < r;
                }

                if (parasLineSpace[i1 + 1] is double downLs && downTop - bottom > downLs + downH * 0.5)
                    downFlag = false;
            }

            if (upFlag && downFlag)
            {
                var (_, _, _, upBottom) = paras[i1 - 1][^1].Bbox;
                var (_, downTop, _, _) = paras[i1 + 1][0].Bbox;
                if (top - upBottom < downTop - bottom)
                    paras[i1 - 1].Add(para[0]);
                else
                    paras[i1 + 1].Insert(0, para[0]);
            }
            else if (upFlag)
            {
                paras[i1 - 1].Add(para[0]);
            }
            else if (downFlag)
            {
                paras[i1 + 1].Insert(0, para[0]);
            }

            if (upFlag || downFlag)
            {
                paras.RemoveAt(i1);
                parasLineSpace.RemoveAt(i1);
            }
        }

        foreach (var para in paras)
        {
            for (int i = 0; i < para.Count - 1; i++)
                para[i].Block.LineEnd = WordSeparator(para[i].Ends.Last, para[i + 1].Ends.First);
            para[^1].Block.LineEnd = "\n";
        }
    }

    private sealed record ParaUnit(
        (double X0, double Y0, double X1, double Y1) Bbox,
        (char First, char Last) Ends,
        OcrLayoutBlock Block);

    private static char FirstChar(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
                return c;
        }

        return '\0';
    }

    private static char LastChar(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    private static string WordSeparator(char letter1, char letter2)
    {
        if (letter1 == '\0' || letter2 == '\0')
            return "\n";

        if (IsCjk(letter1) && IsCjk(letter2))
            return "";

        if (letter1 == '-')
            return "";

        if (IsPunctuation(letter2))
            return "";

        return " ";
    }

    private static bool IsPunctuation(char c)
    {
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjk(char character)
    {
        int code = character;
        return code is >= 0x4E00 and <= 0x9FFF
               or >= 0x3040 and <= 0x30FF
               or >= 0x1100 and <= 0x11FF
               or >= 0x3130 and <= 0x318F
               or >= 0xAC00 and <= 0xD7AF
               or >= 0x3000 and <= 0x303F
               or >= 0xFE30 and <= 0xFE4F
               or >= 0xFF00 and <= 0xFFEF;
    }
}
