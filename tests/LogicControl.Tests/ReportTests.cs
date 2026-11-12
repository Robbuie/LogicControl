using System.Text;
using LogicControl.App.ViewModels;
using LogicControl.Core.Reports;
using Xunit;

namespace LogicControl.Tests;

/// <summary>File > Export as CSV: the tables, and CSV that Excel opens as it should.</summary>
public class ReportTests
{
    [Fact]
    public void CellsAreQuotedTheWayExcelReadsThem()
    {
        var t = new CsvTable("A", "B", "C");
        t.Add("plain", "has, comma", "has \"quotes\"");
        t.Add("two\nlines", null, "Alias → Motor");

        Assert.Equal("A,B,C\r\nplain,\"has, comma\",\"has \"\"quotes\"\"\"\r\n\"two\nlines\",,Alias → Motor\r\n", t.ToCsv());
        Assert.Throws<ArgumentException>(() => t.Add("only one"));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-- spare --", "'-- spare --")]
    [InlineData("-5", "-5")]
    [InlineData("-2.5E3", "-2.5E3")]
    public void ACellExcelWouldRunAsAFormulaIsWrittenAsText(string value, string written) =>
        Assert.Equal(written, CsvTable.Cell(value));

    [Fact]
    public void TheFileIsUtf8WithAByteOrderMark()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}.csv");
        try
        {
            var t = new CsvTable("Kind");
            t.Add("Consumed ← RobotCell");
            t.Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("Kind\r\nConsumed ← RobotCell\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EachTableIsWhatItsTabShows()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));

        foreach (CsvReport report in Enum.GetValues<CsvReport>())
        {
            CsvTable table = CsvReports.Build(main, report);
            Assert.All(table.Rows, r => Assert.Equal(table.Headers.Count, r.Count));
            Assert.NotEmpty(table.Rows);
        }

        Assert.Equal(main.Findings.Count, CsvReports.Build(main, CsvReport.Findings).Rows.Count);
        Assert.Equal(main.Tags.Sum(t => t.Uses.Count), CsvReports.Build(main, CsvReport.CrossReference).Rows.Count);
        Assert.Equal("Line3_PLC tag cross-reference.csv", CsvReports.FileName(main, CsvReport.CrossReference));

        // The filter applies: what is sent is what is on screen.
        main.Filter = "Conveyor_Run";
        CsvTable tags = CsvReports.Build(main, CsvReport.Tags);
        Assert.Equal(main.Tags.Count, tags.Rows.Count);
        Assert.All(tags.Rows, r => Assert.Contains("Conveyor_Run", string.Join(" ", r), StringComparison.OrdinalIgnoreCase));
    }
}
