namespace LogicControl.Core.Analysis;

/// <summary>A point in the system view, in device-independent pixels.</summary>
public readonly record struct PlantPoint(double X, double Y);

/// <summary>Where one node's box is drawn.</summary>
public sealed record PlantBox(PlantNode Node, double X, double Y, double Width, double Height)
{
    public PlantPoint Left => new(X, Y + (Height / 2));

    public PlantPoint Right => new(X + Width, Y + (Height / 2));

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>
/// How one link is drawn: a polyline (I/O tree elbows) or a cubic Bézier (four points: start,
/// two controls, end) for links that cross the trees - produced tags, messages, peers.
/// </summary>
public sealed record PlantRoute(PlantLink Link, IReadOnlyList<PlantPoint> Points, bool IsCurve, PlantPoint LabelAt);

/// <summary>
/// Places the system view: each controller's I/O tree laid out left to right by depth - the
/// controller, then its bridges, then what hangs off them - with leaves stacked in slot and
/// address order and each parent centred on its children. Controllers are stacked one above the
/// other; addresses no opened project knows go in a column on the right. Pure geometry, so it is
/// tested here and the view only strokes it.
/// </summary>
public sealed class PlantLayout
{
    public const double BoxWidth = 210;
    public const double BoxHeight = 48;
    public const double ColumnGap = 64;
    public const double RowGap = 12;
    public const double BlockGap = 44;
    public const double Margin = 24;

    private PlantLayout(IReadOnlyList<PlantBox> boxes, IReadOnlyList<PlantRoute> routes, double width, double height)
    {
        Boxes = boxes;
        Routes = routes;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<PlantBox> Boxes { get; }

    public IReadOnlyList<PlantRoute> Routes { get; }

    public double Width { get; }

    public double Height { get; }

    public static PlantLayout Empty { get; } = new([], [], 0, 0);

    public PlantBox? HitTest(double x, double y) => Boxes.LastOrDefault(b => b.Contains(x, y));

    public static PlantLayout Build(PlantModel plant)
    {
        ArgumentNullException.ThrowIfNull(plant);

        var boxes = new Dictionary<string, PlantBox>(StringComparer.Ordinal);
        ILookup<string?, PlantNode> children = plant.Nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId);
        double top = Margin;
        int deepest = 0;

        foreach (PlantNode root in plant.Nodes.Where(n => n.Kind == PlantNodeKind.Controller))
        {
            double next = top;
            Place(root, 0);
            top = next + BlockGap;

            // Leaves take the next row; a parent sits halfway between its first and last child.
            double Place(PlantNode node, int depth)
            {
                deepest = Math.Max(deepest, depth);
                List<PlantNode> kids = children[node.Id].ToList();
                double y;
                if (kids.Count == 0)
                {
                    y = next;
                    next += BoxHeight + RowGap;
                }
                else
                {
                    List<double> ys = kids.Select(k => Place(k, depth + 1)).ToList();
                    y = (ys[0] + ys[^1]) / 2;
                }

                boxes[node.Id] = new PlantBox(node, Margin + (depth * (BoxWidth + ColumnGap)), y, BoxWidth, BoxHeight);
                return y;
            }
        }

        // Addresses outside every tree: a column of their own, to the right.
        double externalX = Margin + ((deepest + 1) * (BoxWidth + ColumnGap)) + ColumnGap;
        double externalY = Margin;
        foreach (PlantNode node in plant.Nodes.Where(n => n.Kind == PlantNodeKind.External))
        {
            boxes[node.Id] = new PlantBox(node, externalX, externalY, BoxWidth, BoxHeight);
            externalY += BoxHeight + RowGap;
        }

        var routes = new List<PlantRoute>();
        foreach (PlantLink link in plant.Links)
        {
            if (!boxes.TryGetValue(link.From, out PlantBox? from) || !boxes.TryGetValue(link.To, out PlantBox? to))
            {
                continue;
            }

            routes.Add(link.Kind is PlantLinkKind.Tree or PlantLinkKind.Io ? Elbow(link, from, to) : Curve(link, from, to));
        }

        double width = boxes.Count == 0 ? 0 : boxes.Values.Max(b => b.X + b.Width) + Margin + 80;
        double height = boxes.Count == 0 ? 0 : boxes.Values.Max(b => b.Y + b.Height) + Margin;
        return new PlantLayout(boxes.Values.ToList(), routes, width, height);
    }

    private static PlantRoute Elbow(PlantLink link, PlantBox parent, PlantBox child)
    {
        PlantPoint a = parent.Right;
        PlantPoint d = child.Left;
        double mid = a.X + (ColumnGap / 2);
        return new PlantRoute(link, [a, new PlantPoint(mid, a.Y), new PlantPoint(mid, d.Y), d], IsCurve: false,
            LabelAt: new PlantPoint(mid + 4, d.Y - 4));
    }

    /// <summary>
    /// A link across the trees. Forward (target to the right) it runs right edge to left edge;
    /// otherwise it leaves and enters on the right and bows out, clear of the boxes in between.
    /// </summary>
    private static PlantRoute Curve(PlantLink link, PlantBox from, PlantBox to)
    {
        if (to.X > from.X + from.Width)
        {
            PlantPoint s = from.Right;
            PlantPoint e = to.Left;
            double pull = Math.Max(40, (e.X - s.X) / 2);
            return new PlantRoute(link, [s, new PlantPoint(s.X + pull, s.Y), new PlantPoint(e.X - pull, e.Y), e], IsCurve: true,
                LabelAt: new PlantPoint((s.X + e.X) / 2, ((s.Y + e.Y) / 2) - 6));
        }

        PlantPoint start = from.Right;
        PlantPoint end = to.Right;
        double bow = 70 + (Math.Abs(end.Y - start.Y) * 0.12);
        double x = Math.Max(start.X, end.X) + bow;
        return new PlantRoute(link, [start, new PlantPoint(x, start.Y), new PlantPoint(x, end.Y), end], IsCurve: true,
            LabelAt: new PlantPoint(x - (bow * 0.25), (start.Y + end.Y) / 2));
    }
}
