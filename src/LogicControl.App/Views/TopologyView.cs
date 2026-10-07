using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogicControl.Core.Analysis;

namespace LogicControl.App.Views;

/// <summary>
/// Draws the system view: a box per controller, bridge and device, the I/O tree as elbows, and
/// produced tags, messages and peers as curves across it.
///
/// <para>The geometry is <see cref="PlantLayout"/>'s, worked out and tested in the engine; this
/// class strokes it. As in <see cref="LadderRungView"/>, every colour is a dependency property that
/// the <c>Topology</c> style binds to a theme token, so OnRender never looks a colour up.</para>
///
/// <para>Click a box to select it (<see cref="SelectedNode"/>), double-click to run
/// <see cref="OpenCommand"/> with it. Hover a box or a link label for what it is.</para>
/// </summary>
public sealed class TopologyView : FrameworkElement
{
    public static readonly DependencyProperty LayoutDataProperty = Register(nameof(LayoutData), typeof(PlantLayout), PlantLayout.Empty, measure: true);

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(PlantNode), typeof(TopologyView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(TopologyView));

    public static readonly DependencyProperty BoxFillProperty = Register(nameof(BoxFill), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty BoxLineProperty = Register(nameof(BoxLine), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty ControllerFillProperty = Register(nameof(ControllerFill), typeof(Brush), Brushes.Lavender);
    public static readonly DependencyProperty AccentBrushProperty = Register(nameof(AccentBrush), typeof(Brush), Brushes.SlateBlue);
    public static readonly DependencyProperty InkBrushProperty = Register(nameof(InkBrush), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty MutedBrushProperty = Register(nameof(MutedBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty TreeBrushProperty = Register(nameof(TreeBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty IoBrushProperty = Register(nameof(IoBrush), typeof(Brush), Brushes.DimGray);
    public static readonly DependencyProperty ProducedBrushProperty = Register(nameof(ProducedBrush), typeof(Brush), Brushes.SeaGreen);
    public static readonly DependencyProperty MessageBrushProperty = Register(nameof(MessageBrush), typeof(Brush), Brushes.SteelBlue);
    public static readonly DependencyProperty WarnBrushProperty = Register(nameof(WarnBrush), typeof(Brush), Brushes.Goldenrod);
    public static readonly DependencyProperty HoverFillProperty = Register(nameof(HoverFill), typeof(Brush), Brushes.LightGray);
    public static readonly DependencyProperty FontFamilyProperty = Register(nameof(FontFamily), typeof(FontFamily), new FontFamily("Segoe UI"));
    public static readonly DependencyProperty MonoFamilyProperty = Register(nameof(MonoFamily), typeof(FontFamily), new FontFamily("Consolas"));
    public static readonly DependencyProperty FontSizeProperty = Register(nameof(FontSize), typeof(double), 12.0);

    private PlantBox? _hover;

    public TopologyView()
    {
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    public PlantLayout LayoutData { get => (PlantLayout)GetValue(LayoutDataProperty); set => SetValue(LayoutDataProperty, value); }

    public PlantNode? SelectedNode { get => (PlantNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }

    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    public Brush BoxFill { get => (Brush)GetValue(BoxFillProperty); set => SetValue(BoxFillProperty, value); }

    public Brush BoxLine { get => (Brush)GetValue(BoxLineProperty); set => SetValue(BoxLineProperty, value); }

    public Brush ControllerFill { get => (Brush)GetValue(ControllerFillProperty); set => SetValue(ControllerFillProperty, value); }

    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public Brush InkBrush { get => (Brush)GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }

    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    public Brush TreeBrush { get => (Brush)GetValue(TreeBrushProperty); set => SetValue(TreeBrushProperty, value); }

    public Brush IoBrush { get => (Brush)GetValue(IoBrushProperty); set => SetValue(IoBrushProperty, value); }

    public Brush ProducedBrush { get => (Brush)GetValue(ProducedBrushProperty); set => SetValue(ProducedBrushProperty, value); }

    public Brush MessageBrush { get => (Brush)GetValue(MessageBrushProperty); set => SetValue(MessageBrushProperty, value); }

    public Brush WarnBrush { get => (Brush)GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

    public Brush HoverFill { get => (Brush)GetValue(HoverFillProperty); set => SetValue(HoverFillProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public FontFamily MonoFamily { get => (FontFamily)GetValue(MonoFamilyProperty); set => SetValue(MonoFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(LayoutData.Width, LayoutData.Height);

    protected override void OnRender(DrawingContext dc)
    {
        PlantLayout layout = LayoutData;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(ActualWidth, layout.Width), Math.Max(ActualHeight, layout.Height)));

        // Links first, so boxes sit on top of them.
        foreach (PlantRoute route in layout.Routes)
        {
            Pen pen = PenFor(route.Link.Kind);
            var figure = new PathFigure { StartPoint = P(route.Points[0]), IsFilled = false };
            if (route.IsCurve)
            {
                figure.Segments.Add(new BezierSegment(P(route.Points[1]), P(route.Points[2]), P(route.Points[3]), isStroked: true));
            }
            else
            {
                figure.Segments.Add(new PolyLineSegment(route.Points.Skip(1).Select(P), isStroked: true));
            }

            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            if (route.IsCurve)
            {
                Arrow(dc, route.Points[2], route.Points[3], pen.Brush);
            }

            if (route.Link.Kind != PlantLinkKind.Tree && !string.IsNullOrEmpty(route.Link.Label))
            {
                FormattedText label = Text(route.Link.Label, pen.Brush, dpi, FontSize * 0.85, mono: route.Link.Kind == PlantLinkKind.Io);
                Point at = P(route.LabelAt);
                double x = route.IsCurve ? at.X - (label.WidthIncludingTrailingWhitespace / 2) : at.X;
                double y = at.Y - label.Height;
                dc.DrawRectangle(BoxFill, null, new Rect(x - 2, y, label.WidthIncludingTrailingWhitespace + 4, label.Height));
                dc.DrawText(label, new Point(x, y));
            }
        }

        foreach (PlantBox box in layout.Boxes)
        {
            DrawBox(dc, box, dpi);
        }
    }

    private void DrawBox(DrawingContext dc, PlantBox box, double dpi)
    {
        PlantNode n = box.Node;
        bool selected = ReferenceEquals(n, SelectedNode) || (SelectedNode is not null && SelectedNode.Id == n.Id);
        bool controller = n.Kind is PlantNodeKind.Controller or PlantNodeKind.Peer;

        Brush fill = ReferenceEquals(box, _hover) ? HoverFill : controller ? ControllerFill : BoxFill;
        var line = new Pen(selected ? AccentBrush : controller ? AccentBrush : BoxLine, selected ? 2.2 : 1.0);
        if (n.Kind == PlantNodeKind.External || n.Inhibited)
        {
            line.DashStyle = DashStyles.Dash;
        }

        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        dc.DrawRoundedRectangle(fill, line, rect, 6, 6);

        string glyph = n.Kind switch
        {
            PlantNodeKind.Controller or PlantNodeKind.Peer => "",
            PlantNodeKind.Bridge => "",
            PlantNodeKind.External => "",
            _ => "",
        };
        var glyphText = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize + 2, controller ? AccentBrush : MutedBrush, dpi);
        dc.DrawText(glyphText, new Point(box.X + 9, box.Y + ((box.Height - glyphText.Height) / 2)));

        double textX = box.X + 34;
        double width = box.Width - 42;
        FormattedText name = Text(n.Name, InkBrush, dpi, FontSize, mono: false, weight: controller ? FontWeights.SemiBold : FontWeights.Normal);
        name.MaxTextWidth = width;
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(name, new Point(textX, box.Y + 6));

        string sub = string.Join("  ", new[] { n.Address, n.Kind == PlantNodeKind.Peer ? "(opened)" : n.Catalog }.Where(s => !string.IsNullOrEmpty(s)));
        FormattedText detail = Text(sub, MutedBrush, dpi, FontSize * 0.85, mono: true);
        detail.MaxTextWidth = width;
        detail.MaxLineCount = 1;
        detail.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(detail, new Point(textX, box.Y + box.Height - detail.Height - 6));
    }

    private Pen PenFor(PlantLinkKind kind)
    {
        Pen pen = kind switch
        {
            PlantLinkKind.Io => new Pen(IoBrush, 1.4),
            PlantLinkKind.Produced => new Pen(ProducedBrush, 1.8),
            PlantLinkKind.Message => new Pen(MessageBrush, 1.6) { DashStyle = DashStyles.Dash },
            PlantLinkKind.SameDevice => new Pen(AccentBrush, 1.4) { DashStyle = DashStyles.Dot },
            _ => new Pen(TreeBrush, 1.0),
        };
        pen.Freeze();
        return pen;
    }

    private static void Arrow(DrawingContext dc, PlantPoint from, PlantPoint to, Brush brush)
    {
        Vector v = P(to) - P(from);
        if (v.Length < 0.1)
        {
            return;
        }

        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        Point tip = P(to);
        var head = new StreamGeometry();
        using (StreamGeometryContext g = head.Open())
        {
            g.BeginFigure(tip, isFilled: true, isClosed: true);
            g.LineTo(tip - (v * 8) + (n * 4), isStroked: true, isSmoothJoin: false);
            g.LineTo(tip - (v * 8) - (n * 4), isStroked: true, isSmoothJoin: false);
        }

        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private FormattedText Text(string text, Brush brush, double dpi, double size, bool mono, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(mono ? MonoFamily : FontFamily, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    private static Point P(PlantPoint p) => new(p.X, p.Y);

    // ------------------------------------------------------------------ pointer

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point at = e.GetPosition(this);
        PlantBox? hit = LayoutData.HitTest(at.X, at.Y);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Cursor = hit is null ? null : Cursors.Hand;
            ToolTip = hit is null ? null : Describe(hit.Node);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point at = e.GetPosition(this);
        PlantBox? hit = LayoutData.HitTest(at.X, at.Y);
        SelectedNode = hit?.Node;
        if (e.ClickCount == 2 && hit is not null && OpenCommand?.CanExecute(hit.Node) == true)
        {
            OpenCommand.Execute(hit.Node);
        }

        e.Handled = hit is not null;
    }

    private static string Describe(PlantNode n) =>
        string.Join(Environment.NewLine, new[]
        {
            n.Name,
            n.Catalog,
            n.Address,
            n.Kind switch
            {
                PlantNodeKind.Peer => "Another opened controller - double-click to make it the main project",
                PlantNodeKind.External => "A message goes here; it is in no opened I/O tree",
                PlantNodeKind.Controller => "Controller",
                _ => n.Inhibited ? "Inhibited" : "Double-click to open it on the Hardware tab",
            },
        }.Where(s => !string.IsNullOrEmpty(s)));

    private static DependencyProperty Register(string name, Type type, object? fallback, bool measure = false) =>
        DependencyProperty.Register(name, type, typeof(TopologyView), new FrameworkPropertyMetadata(
            fallback,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender : FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((TopologyView)d)._hover = null));
}
