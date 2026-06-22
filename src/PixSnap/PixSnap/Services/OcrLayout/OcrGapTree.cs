namespace PixSnap.Services.OcrLayout;

/// <summary>间隙树排序：多栏阅读顺序。参考 Umi-OCR GapTree_Sort_Algorithm。</summary>
internal sealed class OcrGapTree
{
    private readonly Func<OcrLayoutBlock, (double X0, double Y0, double X1, double Y1)> _getBbox;
    private List<LayoutNode> _currentNodes = [];

    public OcrGapTree(Func<OcrLayoutBlock, (double X0, double Y0, double X1, double Y1)> getBbox) =>
        _getBbox = getBbox;

    public List<OcrLayoutBlock> Sort(List<OcrLayoutBlock> blocks)
    {
        if (blocks.Count == 0)
            return blocks;

        var (units, pageL, pageR) = GetUnits(blocks);
        var (cuts, rows) = GetCutsRows(units, pageL, pageR);
        var root = BuildLayoutTree(cuts, rows);
        _currentNodes = PreorderTraversal(root);
        return GetTextBlocks(_currentNodes);
    }

    public List<List<OcrLayoutBlock>> GetNodesTextBlocks()
    {
        var result = new List<List<OcrLayoutBlock>>();
        foreach (var node in _currentNodes)
        {
            if (node.Units.Count == 0)
            {
                result.Add([]);
                continue;
            }

            result.Add(node.Units.Select(u => u.Block).ToList());
        }

        return result;
    }

    private (List<Unit> Units, double PageL, double PageR) GetUnits(List<OcrLayoutBlock> blocks)
    {
        var units = new List<Unit>();
        double pageL = double.PositiveInfinity;
        double pageR = double.NegativeInfinity;

        foreach (var block in blocks)
        {
            var bbox = _getBbox(block);
            units.Add(new Unit(bbox, block));
            pageL = Math.Min(pageL, bbox.X0);
            pageR = Math.Max(pageR, bbox.X1);
        }

        units.Sort((a, b) => a.Bbox.Y0.CompareTo(b.Bbox.Y0));
        return (units, pageL, pageR);
    }

    private static (List<Cut> Cuts, List<List<Unit>> Rows) GetCutsRows(List<Unit> units, double pageL, double pageR)
    {
        pageL -= 1;
        pageR += 1;
        var rows = new List<List<Unit>>();
        var completedCuts = new List<Cut>();
        var gaps = new List<Gap>();
        int rowIndex = 0;
        int unitIndex = 0;

        while (unitIndex < units.Count)
        {
            var unit = units[unitIndex];
            double uBottom = unit.Bbox.Y1;
            var row = new List<Unit> { unit };

            for (int i = unitIndex + 1; i < units.Count; i++)
            {
                if (units[i].Bbox.Y0 > uBottom)
                    break;
                row.Add(units[i]);
                unitIndex = i;
            }

            row.Sort((a, b) =>
            {
                int cmp = a.Bbox.X0.CompareTo(b.Bbox.X0);
                return cmp != 0 ? cmp : a.Bbox.X1.CompareTo(b.Bbox.X1);
            });

            var rowGaps = new List<Gap>();
            double searchStart = pageL;
            foreach (var u in row)
            {
                if (u.Bbox.X0 > searchStart)
                    rowGaps.Add(new Gap(searchStart, u.Bbox.X0, rowIndex));
                if (u.Bbox.X1 > searchStart)
                    searchStart = u.Bbox.X1;
            }

            rowGaps.Add(new Gap(searchStart, pageR, rowIndex));
            var (newGaps, delGaps) = UpdateGaps(gaps, rowGaps);
            int rowMax = rowIndex - 1;
            foreach (var dg in delGaps)
                completedCuts.Add(new Cut(dg.Left, dg.Right, dg.StartRow, rowMax));

            gaps = newGaps;
            rows.Add(row);
            unitIndex++;
            rowIndex++;
        }

        int lastRow = rows.Count - 1;
        foreach (var g in gaps)
            completedCuts.Add(new Cut(g.Left, g.Right, g.StartRow, lastRow));

        completedCuts.Sort((a, b) => a.Left.CompareTo(b.Left));
        return (completedCuts, rows);
    }

    private static (List<Gap> NewGaps, List<Gap> RemovedGaps) UpdateGaps(List<Gap> gaps1, List<Gap> gaps2)
    {
        var flags1 = gaps1.Select(_ => true).ToList();
        var flags2 = gaps2.Select(_ => true).ToList();
        var newGaps = new List<Gap>();

        for (int i1 = 0; i1 < gaps1.Count; i1++)
        {
            var g1 = gaps1[i1];
            for (int i2 = 0; i2 < gaps2.Count; i2++)
            {
                var g2 = gaps2[i2];
                double interL = Math.Max(g1.Left, g2.Left);
                double interR = Math.Min(g1.Right, g2.Right);
                if (interL <= interR)
                {
                    newGaps.Add(new Gap(interL, interR, g1.StartRow));
                    flags1[i1] = false;
                    flags2[i2] = false;
                }
            }
        }

        for (int i2 = 0; i2 < flags2.Count; i2++)
        {
            if (flags2[i2])
                newGaps.Add(gaps2[i2]);
        }

        var removed = new List<Gap>();
        for (int i1 = 0; i1 < flags1.Count; i1++)
        {
            if (flags1[i1])
                removed.Add(gaps1[i1]);
        }

        return (newGaps, removed);
    }

    private static LayoutNode BuildLayoutTree(List<Cut> cuts, List<List<Unit>> rows)
    {
        var rowsGaps = rows.Select(_ => new List<(double Left, double Right)>()).ToList();
        for (int gI = 0; gI < cuts.Count; gI++)
        {
            var cut = cuts[gI];
            for (int rI = cut.StartRow; rI <= cut.EndRow; rI++)
                rowsGaps[rI].Add((cut.Left, cut.Right));
        }

        var root = new LayoutNode(cuts[0].Left - 1, cuts[^1].Right + 1, -1, -1);
        var completedNodes = new List<LayoutNode> { root };
        var nowNodes = new List<LayoutNode>();

        void Complete(LayoutNode node)
        {
            double nodeR = node.XRight - 2;
            var maxNodes = new List<LayoutNode>();
            double maxR = -2;

            foreach (var comNode in completedNodes)
            {
                if (nodeR < comNode.XLeft || nodeR > comNode.XRight + 0.0001)
                    continue;
                if (comNode.RBottom >= node.RTop)
                    continue;

                if (comNode.RBottom > maxR)
                {
                    maxR = comNode.RBottom;
                    maxNodes = [comNode];
                }
                else if (Math.Abs(comNode.RBottom - maxR) < 0.0001)
                {
                    maxNodes.Add(comNode);
                }
            }

            var parent = maxNodes.MaxBy(n => n.XRight)!;
            parent.Children.Add(node);
            completedNodes.Add(node);
        }

        for (int rI = 0; rI < rows.Count; rI++)
        {
            var row = rows[rI];
            var rowGaps = rowsGaps[rI];
            var newNodes = new List<LayoutNode>();

            foreach (var node in nowNodes)
            {
                bool lFlag = false;
                bool rFlag = false;
                bool completedFlag = false;
                double xLeft = node.XLeft;
                double xRight = node.XRight;

                foreach (var gap in rowGaps)
                {
                    if (Math.Abs(gap.Right - xLeft) < 0.0001)
                        lFlag = true;
                    if (Math.Abs(gap.Left - xRight) < 0.0001)
                        rFlag = true;
                    if (xLeft < gap.Left && gap.Left < xRight || xLeft < gap.Right && gap.Right < xRight)
                    {
                        completedFlag = true;
                        break;
                    }
                }

                if (!lFlag || !rFlag)
                    completedFlag = true;

                if (completedFlag)
                    Complete(node);
                else
                {
                    node.RBottom = rI;
                    newNodes.Add(node);
                }
            }

            nowNodes = newNodes;
            int uI = 0;
            int gI = 0;

            while (uI < row.Count)
            {
                var unit = row[uI];
                double xL = rowGaps[gI].Right;
                double xR = rowGaps[gI + 1].Left;

                if (unit.Bbox.X0 > xR + 0.0001)
                {
                    gI++;
                    continue;
                }

                bool added = false;
                foreach (var node in nowNodes)
                {
                    if (Math.Abs(node.XLeft - xL) < 0.0001 && Math.Abs(node.XRight - xR) < 0.0001)
                    {
                        node.Units.Add(unit);
                        added = true;
                        break;
                    }
                }

                if (added)
                {
                    uI++;
                    continue;
                }

                nowNodes.Add(new LayoutNode(xL, xR, rI, rI) { Units = [unit] });
                uI++;
            }
        }

        foreach (var node in nowNodes)
            Complete(node);

        foreach (var node in completedNodes)
        {
            node.Children.Sort((a, b) => a.XLeft.CompareTo(b.XLeft));
            node.Units.Sort((a, b) => a.Bbox.Y0.CompareTo(b.Bbox.Y0));
        }

        return root;
    }

    private static List<LayoutNode> PreorderTraversal(LayoutNode root)
    {
        var stack = new Stack<LayoutNode>();
        var result = new List<LayoutNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.Add(node);
            for (int i = node.Children.Count - 1; i >= 0; i--)
                stack.Push(node.Children[i]);
        }

        return result;
    }

    private static List<OcrLayoutBlock> GetTextBlocks(List<LayoutNode> nodes)
    {
        var result = new List<OcrLayoutBlock>();
        foreach (var node in nodes)
        {
            foreach (var unit in node.Units)
                result.Add(unit.Block);
        }

        return result;
    }

    private sealed record Unit((double X0, double Y0, double X1, double Y1) Bbox, OcrLayoutBlock Block);

    private sealed record Gap(double Left, double Right, int StartRow);

    private sealed record Cut(double Left, double Right, int StartRow, int EndRow);

    private sealed class LayoutNode
    {
        public LayoutNode(double xLeft, double xRight, int rTop, int rBottom)
        {
            XLeft = xLeft;
            XRight = xRight;
            RTop = rTop;
            RBottom = rBottom;
        }

        public double XLeft { get; }
        public double XRight { get; }
        public int RTop { get; set; }
        public int RBottom { get; set; }
        public List<Unit> Units { get; set; } = [];
        public List<LayoutNode> Children { get; } = [];
    }
}
