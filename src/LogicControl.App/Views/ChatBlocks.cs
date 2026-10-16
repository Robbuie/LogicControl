using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LogicControl.App.ViewModels.Assistant;

namespace LogicControl.App.Views;

/// <summary>
/// A line of chat prose with its **bold** and `code` spans. TextBlock.Inlines cannot be bound, so
/// the runs arrive through <see cref="Runs"/> and are built here. Colours are theme tokens set by
/// resource reference, so a theme change restyles the chat with no rebuild.
/// </summary>
public sealed class ChatRichText : TextBlock
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(
        nameof(Runs), typeof(IReadOnlyList<ChatInline>), typeof(ChatRichText),
        new PropertyMetadata(null, (d, _) => ((ChatRichText)d).Build()));

    public ChatRichText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public IReadOnlyList<ChatInline>? Runs
    {
        get => (IReadOnlyList<ChatInline>?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    private void Build()
    {
        Inlines.Clear();
        foreach (ChatInline piece in Runs ?? [])
        {
            var run = new Run(piece.Text);
            if (piece.Bold)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "txt-0");
            }

            if (piece.Code)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "mono");
                run.SetResourceReference(TextElement.BackgroundProperty, "bg-3");
                run.SetResourceReference(TextElement.ForegroundProperty, "accent-text");
            }

            Inlines.Add(run);
        }
    }
}

/// <summary>
/// A markdown table from a chat answer, drawn as a grid: a header row on a raised fill, a hairline
/// between rows, columns as wide as their content up to a cap and wrapping beyond it.
///
/// <para>Every column can be dragged wider or narrower by the divider at its right edge in the
/// header, and a double-click on the divider fits the column to its longest cell without
/// wrapping. A cell whose text is cut short (it never is once wrapped, but a long unbroken word
/// can be) shows it whole in a tooltip. Selecting text is not offered; the message's Copy button
/// copies the table as text.</para>
///
/// <para>Why the cap is on the cells, not the columns: a Grid measures an Auto column's cells
/// with unlimited width, so a cap on the column alone (MaxWidth) left each cell laid out as one
/// long line and then clipped it at the cap - the text past the column's edge was simply not
/// there. A cell with its own MaxWidth wraps.</para>
/// </summary>
public sealed class ChatTable : Grid
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<IReadOnlyList<string>>), typeof(ChatTable),
        new PropertyMetadata(null, (d, _) => ((ChatTable)d).Build()));

    /// <summary>The widest a column grows on its own before its text wraps.</summary>
    private const double MaxColumnWidth = 260;

    /// <summary>The narrowest a column can be dragged.</summary>
    private const double MinColumnWidth = 40;

    /// <summary>A cell's left and right padding together.</summary>
    private const double CellPadding = 18;

    private readonly List<List<TextBlock>> _columns = [];

    public IReadOnlyList<IReadOnlyList<string>>? Rows
    {
        get => (IReadOnlyList<IReadOnlyList<string>>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        _columns.Clear();

        IReadOnlyList<IReadOnlyList<string>> rows = Rows ?? [];
        if (rows.Count == 0)
        {
            return;
        }

        int columns = rows.Max(r => r.Count);
        for (int c = 0; c < columns; c++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _columns.Add([]);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var band = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            band.SetResourceReference(Border.BorderBrushProperty, "line-soft");
            if (r == 0)
            {
                band.SetResourceReference(Border.BackgroundProperty, "bg-3");
            }

            SetRow(band, r);
            SetColumnSpan(band, columns);
            Children.Add(band);

            for (int c = 0; c < columns; c++)
            {
                string text = c < rows[r].Count ? rows[r][c] : string.Empty;
                var cell = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = MaxColumnWidth - CellPadding,
                    Margin = new Thickness(8, 4, 10, 4),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = text.Length > 40 ? text : null,
                };
                cell.SetResourceReference(TextBlock.ForegroundProperty, r == 0 ? "txt-0" : "txt-1");
                SetRow(cell, r);
                SetColumn(cell, c);
                Children.Add(cell);
                _columns[c].Add(cell);
            }
        }

        // A divider at the right edge of every column, the full height of the table: drag it to
        // resize the column, double-click it to fit the column to its text.
        for (int c = 0; c < columns; c++)
        {
            var grip = new Thumb
            {
                Width = 7,
                Margin = new Thickness(0, 0, -3, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.SizeWE,
                Template = GripTemplate,
                ToolTip = "Drag to resize - double-click to fit",
                Tag = c,
            };
            grip.DragStarted += OnGripStarted;
            grip.DragDelta += OnGripDelta;
            grip.MouseDoubleClick += OnGripDoubleClick;
            SetRow(grip, 0);
            SetRowSpan(grip, rows.Count);
            SetColumn(grip, c);
            Children.Add(grip);
        }
    }

    /// <summary>
    /// The first drag fixes every column at the width it has now, so dragging one moves only that
    /// one; from then on a column's cells wrap at whatever width it is given.
    /// </summary>
    private void OnGripStarted(object sender, DragStartedEventArgs e)
    {
        for (int c = 0; c < ColumnDefinitions.Count; c++)
        {
            ColumnDefinition column = ColumnDefinitions[c];
            if (!column.Width.IsAbsolute)
            {
                column.Width = new GridLength(Math.Max(MinColumnWidth, column.ActualWidth));
                foreach (TextBlock cell in _columns[c])
                {
                    cell.MaxWidth = double.PositiveInfinity;
                }
            }
        }
    }

    private void OnGripDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { Tag: int c } && c < ColumnDefinitions.Count)
        {
            ColumnDefinition column = ColumnDefinitions[c];
            double now = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
            column.Width = new GridLength(Math.Max(MinColumnWidth, now + e.HorizontalChange));
        }
    }

    /// <summary>Fits the column to its longest cell: no wrapping, the table scrolls sideways instead.</summary>
    private void OnGripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Thumb { Tag: int c } && c < ColumnDefinitions.Count)
        {
            foreach (TextBlock cell in _columns[c])
            {
                cell.MaxWidth = double.PositiveInfinity;
            }

            ColumnDefinitions[c].Width = GridLength.Auto;
            e.Handled = true;
        }
    }

    /// <summary>A transparent strip with a hairline that lights up under the pointer.</summary>
    private static ControlTemplate GripTemplate { get; } = MakeGripTemplate();

    private static ControlTemplate MakeGripTemplate()
    {
        var strip = new FrameworkElementFactory(typeof(Border));
        strip.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var line = new FrameworkElementFactory(typeof(Border));
        line.Name = "line";
        line.SetValue(WidthProperty, 1.0);
        line.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "line-soft");
        strip.AppendChild(line);

        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = strip };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("accent-line"), "line"));
        template.Triggers.Add(hover);
        template.Seal();
        return template;
    }
}
