using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogicControl.Core.Logic;

namespace LogicControl.App.Views;

/// <summary>
/// Draws one rung as ladder: rails, wires, contacts, coils and instruction boxes.
///
/// <para>The geometry comes from <see cref="LadderLayout"/> in the engine; this class only measures
/// text in the current font and strokes what the layout returns. Every colour arrives through a
/// dependency property that the <c>LadderRung</c> style in Controls.xaml binds to a theme token with
/// <c>{DynamicResource}</c>, so a theme or accent change repaints every rung - OnRender itself never
/// looks a colour up.</para>
///
/// <para>Clicking an operand runs <see cref="OperandCommand"/> with the operand's text; the main
/// window uses it to jump to that tag. Hovering an instruction shows what it is.</para>
/// </summary>
public sealed class LadderRungView : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), null, measure: true);

    public static readonly DependencyProperty ShapesProperty = Register(nameof(Shapes), typeof(Func<string, InstructionShape?>), null, measure: true);

    /// <summary>
    /// The width of the scrolling list the rung sits in. Inside a horizontally scrolling list the
    /// rung is measured with infinite width, which would leave nothing to push the outputs against;
    /// this gives it the visible width instead, less <see cref="ViewportInset"/> for the gutter.
    /// </summary>
    public static readonly DependencyProperty ViewportWidthProperty = Register(nameof(ViewportWidth), typeof(double), 0.0, measure: true);

    public static readonly DependencyProperty ViewportInsetProperty = Register(nameof(ViewportInset), typeof(double), 0.0, measure: true);

    public static readonly DependencyProperty OperandCommandProperty = DependencyProperty.Register(
        nameof(OperandCommand), typeof(ICommand), typeof(LadderRungView));

    public static readonly DependencyProperty WireBrushProperty = Register(nameof(WireBrush), typeof(Brush), Brushes.Gray);

    public static readonly DependencyProperty RailBrushProperty = Register(nameof(RailBrush), typeof(Brush), Brushes.Gray);

    public static readonly DependencyProperty InkBrushProperty = Register(nameof(InkBrush), typeof(Brush), Brushes.Black);

    public static readonly DependencyProperty MutedBrushProperty = Register(nameof(MutedBrush), typeof(Brush), Brushes.Gray);

    public static readonly DependencyProperty AccentBrushProperty = Register(nameof(AccentBrush), typeof(Brush), Brushes.SlateBlue);

    public static readonly DependencyProperty BoxFillProperty = Register(nameof(BoxFill), typeof(Brush), Brushes.Transparent);

    public static readonly DependencyProperty HoverFillProperty = Register(nameof(HoverFill), typeof(Brush), Brushes.Transparent);

    public static readonly DependencyProperty ProblemBrushProperty = Register(nameof(ProblemBrush), typeof(Brush), Brushes.Red);

    public static readonly DependencyProperty FontFamilyProperty = Register(nameof(FontFamily), typeof(FontFamily), new FontFamily("Consolas"), measure: true);

    public static readonly DependencyProperty FontSizeProperty = Register(nameof(FontSize), typeof(double), 12.0, measure: true);

    private LadderDiagram? _diagram;
    private LadderRung? _rung;
    private double _laidOutWidth = -1;
    private LadderElement? _hover;
    private LadderBoxRow? _hoverRow;

    public LadderRungView()
    {
        // Hit testing needs a background; a transparent fill in OnRender gives one.
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 400);
    }

    /// <summary>The rung's neutral text, e.g. XIC(Start)OTE(Motor);</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Looks up an instruction's shape - the built-in table plus the project's AOIs.</summary>
    public Func<string, InstructionShape?>? Shapes
    {
        get => (Func<string, InstructionShape?>?)GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    public double ViewportWidth
    {
        get => (double)GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    public double ViewportInset
    {
        get => (double)GetValue(ViewportInsetProperty);
        set => SetValue(ViewportInsetProperty, value);
    }

    public ICommand? OperandCommand
    {
        get => (ICommand?)GetValue(OperandCommandProperty);
        set => SetValue(OperandCommandProperty, value);
    }

    public Brush WireBrush { get => (Brush)GetValue(WireBrushProperty); set => SetValue(WireBrushProperty, value); }

    public Brush RailBrush { get => (Brush)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }

    public Brush InkBrush { get => (Brush)GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }

    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public Brush BoxFill { get => (Brush)GetValue(BoxFillProperty); set => SetValue(BoxFillProperty, value); }

    public Brush HoverFill { get => (Brush)GetValue(HoverFillProperty); set => SetValue(HoverFillProperty, value); }

    public Brush ProblemBrush { get => (Brush)GetValue(ProblemBrushProperty); set => SetValue(ProblemBrushProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    // ------------------------------------------------------------------ layout

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width)
            ? Math.Max(0, ViewportWidth - ViewportInset)
            : availableSize.Width;
        LadderDiagram diagram = Layout(width);
        return new Size(Math.Max(diagram.Width, width), diagram.Height + ProblemHeight());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width);
        return finalSize;
    }

    private LadderDiagram Layout(double width)
    {
        _rung ??= LadderParser.Parse(Text);

        if (_diagram is null || Math.Abs(_laidOutWidth - width) > 0.5)
        {
            _diagram = LadderLayout.Arrange(_rung, MeasureText, Shapes, minWidth: Math.Max(0, width - 1));
            _laidOutWidth = width;
        }

        return _diagram;
    }

    private double ProblemHeight() => _rung is { IsValid: false } rung ? rung.Problems.Count * (FontSize + 6) + 4 : 0;

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        LadderDiagram d = Layout(ActualWidth);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // A background so the whole rung takes the pointer, not just the strokes.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var rail = new Pen(RailBrush, 3);
        var wire = new Pen(WireBrush, 1.4);
        var ink = new Pen(InkBrush, 1.8);
        var accent = new Pen(AccentBrush, 1.2);
        rail.Freeze();
        wire.Freeze();
        ink.Freeze();
        accent.Freeze();

        double right = Math.Max(d.Width, ActualWidth - 1);
        dc.DrawLine(rail, new Point(1.5, 0), new Point(1.5, d.Height));
        dc.DrawLine(rail, new Point(right - 1.5, 0), new Point(right - 1.5, d.Height));

        foreach (LadderWire w in d.Wires)
        {
            dc.DrawLine(wire, new Point(w.X1, w.Y1), new Point(Math.Min(w.X2, right), w.Y2));
        }

        foreach (LadderElement e in d.Elements)
        {
            if (ReferenceEquals(e, _hover))
            {
                dc.DrawRoundedRectangle(HoverFill, null, Rect(e.Bounds), 4, 4);
            }

            // Lead wires either side of the symbol.
            dc.DrawLine(wire, new Point(e.Bounds.X, e.WireY), new Point(e.Symbol.X, e.WireY));
            dc.DrawLine(wire, new Point(e.Symbol.Right, e.WireY), new Point(e.Bounds.Right, e.WireY));

            switch (e.Shape.Symbol)
            {
                case LadderSymbol.Contact:
                    DrawContact(dc, e, ink, wire, dpi);
                    break;

                case LadderSymbol.Coil:
                    DrawCoil(dc, e, ink, dpi);
                    break;

                default:
                    DrawBox(dc, e, accent, dpi);
                    break;
            }
        }

        if (_rung is { IsValid: false } rung)
        {
            double y = d.Height + 2;
            foreach (string problem in rung.Problems)
            {
                FormattedText text = Format(problem, ProblemBrush, dpi);
                dc.DrawText(text, new Point(14, y));
                y += FontSize + 6;
            }
        }
    }

    private void DrawContact(DrawingContext dc, LadderElement e, Pen ink, Pen wire, double dpi)
    {
        LadderRect s = e.Symbol;
        const double inset = 9;

        dc.DrawLine(wire, new Point(s.X, e.WireY), new Point(s.X + inset, e.WireY));
        dc.DrawLine(wire, new Point(s.Right - inset, e.WireY), new Point(s.Right, e.WireY));
        dc.DrawLine(ink, new Point(s.X + inset, s.Y), new Point(s.X + inset, s.Bottom));
        dc.DrawLine(ink, new Point(s.Right - inset, s.Y), new Point(s.Right - inset, s.Bottom));

        if (e.Glyph == "/")
        {
            dc.DrawLine(ink, new Point(s.X + inset + 3, s.Bottom - 3), new Point(s.Right - inset - 3, s.Y + 3));
        }
        else if (e.Glyph is { } glyph)
        {
            DrawCentred(dc, glyph, MutedBrush, (s.X + s.Right) / 2, s.Bottom + 1, dpi, small: true);
        }

        DrawLabel(dc, e, dpi);
    }

    private void DrawCoil(DrawingContext dc, LadderElement e, Pen ink, double dpi)
    {
        LadderRect s = e.Symbol;
        double r = s.Height / 2;
        double cx = (s.X + s.Right) / 2;

        // Two arcs: ( and ).
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(cx - 4, s.Y), false, false);
            g.ArcTo(new Point(cx - 4, s.Bottom), new Size(r * 0.8, r), 0, false, SweepDirection.Counterclockwise, true, true);
            g.BeginFigure(new Point(cx + 4, s.Y), false, false);
            g.ArcTo(new Point(cx + 4, s.Bottom), new Size(r * 0.8, r), 0, false, SweepDirection.Clockwise, true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, ink, geometry);

        if (e.Glyph is { Length: 1 } glyph)
        {
            DrawCentred(dc, glyph, InkBrush, cx, e.WireY - FontSize * 0.68, dpi, small: true);
        }
        else if (e.Glyph is { } word)
        {
            DrawCentred(dc, word, MutedBrush, cx, s.Bottom + 1, dpi, small: true);
        }

        DrawLabel(dc, e, dpi);
    }

    private void DrawLabel(DrawingContext dc, LadderElement e, double dpi)
    {
        double cx = (e.Symbol.X + e.Symbol.Right) / 2;
        Brush brush = ReferenceEquals(e, _hover) ? AccentBrush : InkBrush;
        DrawCentred(dc, e.Label, brush, cx, e.Symbol.Y - FontSize - 5, dpi, small: false);
    }

    private void DrawBox(DrawingContext dc, LadderElement e, Pen accent, double dpi)
    {
        var box = Rect(e.Symbol);
        dc.DrawRoundedRectangle(BoxFill, accent, box, 3, 3);

        LadderMetrics m = LadderMetrics.Default;
        FormattedText title = Format(e.Label, AccentBrush, dpi);
        title.SetFontWeight(FontWeights.SemiBold);
        dc.DrawText(title, new Point(box.X + m.BoxPad, box.Y + (m.BoxTitleHeight - title.Height) / 2));

        foreach (LadderBoxRow row in e.Rows)
        {
            double y = box.Y + m.BoxTitleHeight + row.Index * m.BoxRowHeight;
            FormattedText name = Format(row.Name, MutedBrush, dpi);
            bool hot = ReferenceEquals(row, _hoverRow);
            FormattedText value = Format(row.Value, hot ? AccentBrush : InkBrush, dpi);
            if (hot)
            {
                value.SetTextDecorations(TextDecorations.Underline);
            }

            dc.DrawText(name, new Point(box.X + m.BoxPad, y));
            dc.DrawText(value, new Point(box.Right - m.BoxPad - value.WidthIncludingTrailingWhitespace, y));
        }
    }

    private void DrawCentred(DrawingContext dc, string text, Brush brush, double cx, double y, double dpi, bool small)
    {
        if (text.Length == 0)
        {
            return;
        }

        FormattedText f = Format(text, brush, dpi, small ? FontSize * 0.85 : FontSize);
        dc.DrawText(f, new Point(cx - f.WidthIncludingTrailingWhitespace / 2, y));
    }

    private FormattedText Format(string text, Brush brush, double dpi, double? size = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size ?? FontSize, brush, dpi);

    private double MeasureText(string text) =>
        text.Length == 0 ? 0 : Format(text, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    private static Rect Rect(LadderRect r) => new(r.X, r.Y, r.Width, r.Height);

    // ------------------------------------------------------------------ pointer

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        LadderElement? hit = _diagram?.HitTest(p.X, p.Y);
        LadderBoxRow? row = hit is { IsBox: true } ? RowAt(hit, p.Y) : null;

        if (!ReferenceEquals(hit, _hover) || !ReferenceEquals(row, _hoverRow))
        {
            _hover = hit;
            _hoverRow = row;
            Cursor = OperandOf(hit, row) is null ? null : Cursors.Hand;
            ToolTip = hit is null ? null : $"{hit.Shape.Mnemonic} - {hit.Shape.Summary}";
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            _hoverRow = null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (OperandOf(_hover, _hoverRow) is { } operand && OperandCommand is { } command && command.CanExecute(operand))
        {
            command.Execute(operand);
            e.Handled = true;
        }
    }

    private static LadderBoxRow? RowAt(LadderElement box, double y)
    {
        LadderMetrics m = LadderMetrics.Default;
        int index = (int)Math.Floor((y - box.Symbol.Y - m.BoxTitleHeight) / m.BoxRowHeight);
        return index >= 0 && index < box.Rows.Count ? box.Rows[index] : null;
    }

    /// <summary>The operand a click means: the contact's or coil's tag, or the box row under the pointer.</summary>
    private static string? OperandOf(LadderElement? element, LadderBoxRow? row)
    {
        if (element is null)
        {
            return null;
        }

        string? text = element.IsBox ? row?.Value : element.Label;
        return string.IsNullOrWhiteSpace(text) || text == "?" ? null : text;
    }

    private static DependencyProperty Register(string name, Type type, object? fallback, bool measure = false)
    {
        FrameworkPropertyMetadataOptions options = measure
            ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender
            : FrameworkPropertyMetadataOptions.AffectsRender;

        return DependencyProperty.Register(name, type, typeof(LadderRungView), new FrameworkPropertyMetadata(fallback, options, OnChanged));
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LadderRungView)d;
        if (e.Property == TextProperty)
        {
            view._rung = null;
        }

        if (e.Property == TextProperty || e.Property == ShapesProperty || e.Property == FontFamilyProperty
            || e.Property == FontSizeProperty || e.Property == ViewportWidthProperty || e.Property == ViewportInsetProperty)
        {
            view._diagram = null;
            view._hover = null;
            view._hoverRow = null;
        }
    }
}
