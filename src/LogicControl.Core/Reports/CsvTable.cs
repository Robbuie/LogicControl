using System.Text;

namespace LogicControl.Core.Reports;

/// <summary>
/// A table written as CSV for Excel: a header row, then one row per item.
///
/// <para>RFC 4180 quoting - a cell with a comma, a quote or a line break is quoted and its quotes
/// doubled - CRLF line ends, and UTF-8 with a byte-order mark, which is what tells Excel the file
/// is UTF-8 (without it "Alias → Motor" opens as mojibake).</para>
///
/// <para>A cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> would be read by Excel
/// as a formula. Descriptions and comments come from the export, written by anybody, so such a
/// cell is written with a leading apostrophe, which Excel shows as text. A plain number such as
/// <c>-5</c> is left as it is.</para>
/// </summary>
public sealed class CsvTable
{
    private readonly List<IReadOnlyList<string>> _rows = [];

    public CsvTable(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Headers = headers;
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;

    /// <summary>Adds a row; it must have one cell per header.</summary>
    public void Add(params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != Headers.Count)
        {
            throw new ArgumentException($"A row needs {Headers.Count} cells; this one has {cells.Length}.", nameof(cells));
        }

        _rows.Add(cells.Select(c => c ?? string.Empty).ToList());
    }

    public string ToCsv()
    {
        var sb = new StringBuilder();
        Line(sb, Headers);
        foreach (IReadOnlyList<string> row in _rows)
        {
            Line(sb, row);
        }

        return sb.ToString();
    }

    /// <summary>Writes the table to <paramref name="path"/> as UTF-8 with a byte-order mark.</summary>
    public void Save(string path) => File.WriteAllText(path, ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static void Line(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Cell(cells[i]));
        }

        sb.Append("\r\n");
    }

    internal static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
