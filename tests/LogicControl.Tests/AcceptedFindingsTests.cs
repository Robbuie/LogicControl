using LogicControl.App.Composition;
using LogicControl.App.ViewModels;
using Xunit;

namespace LogicControl.Tests;

/// <summary>Findings somebody has accepted stay out of the list, for that controller, next time too.</summary>
public class AcceptedFindingsTests
{
    [Fact]
    public async Task AnAcceptedFindingLeavesTheListAndStaysAcceptedNextTime()
    {
        string file = Path.Combine(Path.GetTempPath(), $"lc-accepted-{Guid.NewGuid():N}.json");
        try
        {
            var main = new MainViewModel(accepted: new AcceptedFindings(file));
            await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
            int before = main.Findings.Count;
            FindingRowViewModel row = main.Findings[0];
            Assert.True(row.CanAccept);

            row.IsAccepted = true;
            Assert.Equal(before - 1, main.Findings.Count);
            Assert.DoesNotContain(row, main.Findings);
            Assert.Equal($"Findings  {before - 1}", main.FindingsHeader);
            Assert.Equal("Show accepted (1)", main.ShowAcceptedText);

            main.ShowAccepted = true;
            Assert.Contains(row, main.Findings);
            Assert.Equal("Yes", CsvReports.Build(main, CsvReport.Findings).Rows.Single(r => r[1] == row.Rule && r[3] == row.Subject)[6]);

            // Opened again, by another window: still accepted, still out of the list.
            var again = new MainViewModel(accepted: new AcceptedFindings(file));
            await again.OpenAsync(Fixture.PathOf("Line3.L5X"));
            Assert.Equal(before - 1, again.Findings.Count);
            Assert.DoesNotContain(again.Findings, f => f.Finding.Identity == row.Finding.Identity);

            // Taken back.
            again.ShowAccepted = true;
            again.Findings.Single(f => f.Finding.Identity == row.Finding.Identity).IsAccepted = false;
            again.ShowAccepted = false;
            Assert.Equal(before, again.Findings.Count);
            var third = new MainViewModel(accepted: new AcceptedFindings(file));
            await third.OpenAsync(Fixture.PathOf("Line3.L5X"));
            Assert.Equal(before, third.Findings.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AnotherControllerKeepsItsOwnList()
    {
        var accepted = new AcceptedFindings();
        var main = new MainViewModel(accepted: accepted);
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        foreach (FindingRowViewModel f in main.Findings.ToList())
        {
            f.IsAccepted = true;
        }

        Assert.Empty(main.Findings);
        await main.OpenAsync(Fixture.PathOf("RobotCell.L5X"));
        Assert.NotEmpty(main.Findings);
        Assert.All(main.Findings, f => Assert.False(f.IsAccepted));
    }

    [Fact]
    public void AFindingIsKnownByWhatAndWhereNotItsWording()
    {
        var a = new Core.Analysis.Finding(Core.Analysis.FindingSeverity.Warning, "LC-LOG-001", "Logic", "Motor", "Written in 3 places", "MainProgram/Motors");
        Assert.Equal(a.Identity, (a with { Message = "Written in 4 places" }).Identity);
        Assert.NotEqual(a.Identity, (a with { Location = "MainProgram/Other" }).Identity);
    }
}
