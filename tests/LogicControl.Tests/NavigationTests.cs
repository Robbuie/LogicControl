using LogicControl.App.ViewModels;
using LogicControl.Core.Analysis;
using Xunit;

namespace LogicControl.Tests;

/// <summary>Jumping from a finding or a cross-reference to the rung it is about.</summary>
public class NavigationTests
{
    private static MainViewModel Opened()
    {
        var main = new MainViewModel();
        main.Load(Fixture.Line3Analysed);
        return main;
    }

    [Fact]
    public void AFindingOpensItsRoutineAtTheRung()
    {
        MainViewModel main = Opened();
        int? scrolled = null;
        main.LineFocusRequested += (_, index) => scrolled = index;

        FindingRowViewModel coil = main.Findings.Single(f => f.Rule == "LC-LOG-001");
        Assert.True(coil.HasSites);
        Assert.True(main.OpenSiteCommand.CanExecute(coil));

        main.OpenSiteCommand.Execute(coil.Sites[1]);

        Assert.Equal(MainViewModel.LogicTab, main.SelectedTab);
        Assert.Equal("MainProgram/Motors", main.Routine!.Title);
        Assert.Equal(3, scrolled);
        Assert.True(main.Routine.Lines[3].IsHighlighted);
        Assert.Equal(1, main.Routine.Lines.Count(l => l.IsHighlighted));
    }

    [Fact]
    public void AStructuredTextSiteMarksTheLine()
    {
        MainViewModel main = Opened();
        Assert.True(main.OpenSite(new FindingSite("MainProgram/Calc", 2, IsStructuredText: true)));
        Assert.True(main.Routine!.Lines[2].IsHighlighted);
        Assert.True(main.TextVisible);
    }

    [Fact]
    public void ARoutineFindingOpensTheRoutineWithNothingMarked()
    {
        MainViewModel main = Opened();
        FindingRowViewModel uncalled = main.Findings.Single(f => f.Rule == "LC-LOG-007");
        main.OpenSiteCommand.Execute(uncalled);
        Assert.Equal("MainProgram/OldLogic", main.Routine!.Title);
        Assert.DoesNotContain(main.Routine.Lines, l => l.IsHighlighted);
    }

    [Fact]
    public void ACrossReferenceRowOpensTheRung()
    {
        MainViewModel main = Opened();
        TagRowViewModel speed = main.Tags.Single(t => t.Name == "Speed_Ref");
        TagUseRowViewModel fromFast = speed.Uses.Single(u => u.Routine == "Fast/MainRoutine");

        main.OpenSiteCommand.Execute(fromFast);

        Assert.Equal("Fast/MainRoutine", main.Routine!.Title);
        Assert.True(main.Routine.Lines[1].IsHighlighted);
    }

    [Fact]
    public void HardwareFindingsAndAliasRowsHaveNowhereToGo()
    {
        MainViewModel main = Opened();
        Assert.False(main.OpenSiteCommand.CanExecute(main.Findings.Single(f => f.Rule == "LC-HW-001")));
        TagUseRowViewModel alias = main.Tags.Single(t => t.Name == "E_Stop_OK").Uses.First();
        Assert.False(main.OpenSite(new FindingSite("Nowhere/Missing", 0)));
        Assert.NotNull(alias);
    }
}
