using System.IO;
using System.Globalization;
using LogicControl.Core.Reports;

namespace LogicControl.App.ViewModels;

/// <summary>The tables File > Export as CSV writes.</summary>
public enum CsvReport
{
    Findings,
    Tags,
    CrossReference,
    Hardware,
    Communications,
}

/// <summary>
/// The window's tables as CSV, for Excel: what each tab shows, with the filter applied, so what
/// is sent is what is on screen. The cross-reference is every use of every tag shown, one row
/// each - the table a contractor asks for.
/// </summary>
public static class CsvReports
{
    /// <summary>"Findings", "Tag cross-reference" - the file name's second half and the menu's words.</summary>
    public static string Title(CsvReport report) => report switch
    {
        CsvReport.Findings => "Findings",
        CsvReport.Tags => "Tags",
        CsvReport.CrossReference => "Tag cross-reference",
        CsvReport.Hardware => "Hardware",
        CsvReport.Communications => "Communications",
        _ => report.ToString(),
    };

    public static CsvTable Build(MainViewModel main, CsvReport report)
    {
        ArgumentNullException.ThrowIfNull(main);
        switch (report)
        {
            case CsvReport.Findings:
            {
                var t = new CsvTable("Severity", "Rule", "Category", "Subject", "Message", "Where", "Accepted");
                foreach (FindingRowViewModel f in main.Findings)
                {
                    string where = f.HasSites ? string.Join("; ", f.Sites.Select(s => s.Text)) : f.Location;
                    t.Add(f.Finding.Severity.ToString(), f.Rule, f.Category, f.Subject, f.Message, where, f.IsAccepted ? "Yes" : string.Empty);
                }

                return t;
            }

            case CsvReport.Tags:
            {
                var t = new CsvTable("Name", "Scope", "Data type", "Kind", "Description", "Reads", "Writes", "Usage");
                foreach (TagRowViewModel r in main.Tags)
                {
                    t.Add(r.Name, r.Scope, r.DataType, r.Kind, r.Description, Number(r.Reads), Number(r.Writes), r.Usage);
                }

                return t;
            }

            case CsvReport.CrossReference:
            {
                var t = new CsvTable("Tag", "Scope", "Routine", "Where", "Instruction", "Operand", "Access");
                foreach (TagRowViewModel r in main.Tags)
                {
                    foreach (TagUseRowViewModel u in r.Uses)
                    {
                        t.Add(r.Name, r.Scope, u.Routine, u.Location, u.Instruction, u.Operand, u.Access);
                    }
                }

                return t;
            }

            case CsvReport.Hardware:
            {
                var t = new CsvTable("Module", "Catalog", "Address", "Revision", "RPI", "Connection", "Keying", "Logic refs", "Notes", "Description", "Path");
                foreach (HardwareRowViewModel h in main.Hardware)
                {
                    t.Add(h.Name, h.Catalog, h.Address, h.Revision, h.Rpi, h.Connection, h.Keying, Number(h.LogicReferences), h.Flags, h.Description, h.Path);
                }

                return t;
            }

            case CsvReport.Communications:
            {
                var t = new CsvTable("Kind", "From", "To", "Address", "RPI", "What moves", "Configured at");
                foreach (CommRowViewModel c in main.Communications)
                {
                    t.Add(c.Kind, c.From, c.To, c.Address, c.Rpi, c.Detail, c.Source);
                }

                return t;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(report));
        }
    }

    /// <summary>"Line3 findings.csv" - the controller's name and what the table is.</summary>
    public static string FileName(MainViewModel main, CsvReport report)
    {
        ArgumentNullException.ThrowIfNull(main);
        string controller = main.Analysis?.Project.Controller.Name ?? "LogicControl";
        string name = $"{controller} {Title(report).ToLowerInvariant()}.csv";
        return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
