using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
/// between rows, columns as wide as their content up to a cap and wrapping beyond it. Selecting
/// text is not offered; the message's Copy button copies the table as text.
/// </summary>
public sealed class ChatTable : Grid
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<IReadOnlyList<string>>), typeof(ChatTable),
        new PropertyMetadata(null, (d, _) => ((ChatTable)d).Build()));

    /// <summary>The widest a column grows before its text wraps.</summary>
    private const double MaxColumnWidth = 220;

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

        IReadOnlyList<IReadOnlyList<string>> rows = Rows ?? [];
        if (rows.Count == 0)
        {
            return;
        }

        int columns = rows.Max(r => r.Count);
        for (int c = 0; c < columns; c++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = MaxColumnWidth });
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
                var cell = new TextBlock
                {
                    Text = c < rows[r].Count ? rows[r][c] : string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 4, 10, 4),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                };
                cell.SetResourceReference(TextBlock.ForegroundProperty, r == 0 ? "txt-0" : "txt-1");
                SetRow(cell, r);
                SetColumn(cell, c);
                Children.Add(cell);
            }
        }
    }
}
