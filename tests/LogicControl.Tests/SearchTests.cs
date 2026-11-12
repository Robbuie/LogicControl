using LogicControl.App.ViewModels;
using LogicControl.Core.Analysis;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The Search tab: text or a tag across every routine.</summary>
public class SearchTests
{
    [Fact]
    public void TextIsFoundInRungsCommentsAndStructuredText()
    {
        IReadOnlyList<SearchHit> hits = LogicSearch.Find(Fixture.Line3, "conveyor_run");
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains("Conveyor_Run", h.Text, StringComparison.OrdinalIgnoreCase));

        SearchHit comment = Assert.Single(LogicSearch.Find(Fixture.Line3, "commissioning"));
        Assert.Equal(SearchHitKind.Comment, comment.Kind);
        Assert.False(comment.Site.IsStructuredText);

        SearchHit st = Assert.Single(LogicSearch.Find(Fixture.Line3, "Speed_Ref := Line"));
        Assert.True(st.Site.IsStructuredText);
        Assert.Equal(1, st.Site.Location);
        Assert.EndsWith("/Calc", st.Site.Routine, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeTagNameMatchesTheTagAndItsMembersOnly()
    {
        // As text, part of a name is found; as a tag, only the name itself, in ladder or ST.
        Assert.NotEmpty(LogicSearch.Find(Fixture.Line3, "Line_Speed"));
        Assert.Empty(LogicSearch.Find(Fixture.Line3, "Line_Speed", wholeTag: true));
        Assert.Single(LogicSearch.Find(Fixture.Line3, "line_speed_pct", wholeTag: true));

        Assert.NotEmpty(LogicSearch.Find(Fixture.Line3, "Conveyor"));
        Assert.Empty(LogicSearch.Find(Fixture.Line3, "Conveyor", wholeTag: true));
        IReadOnlyList<SearchHit> run = LogicSearch.Find(Fixture.Line3, "Conveyor_Run", wholeTag: true);
        Assert.NotEmpty(run);
        Assert.All(run, h => Assert.Equal(SearchHitKind.Logic, h.Kind));
    }

    [Fact]
    public void ABigSearchStopsAtTheLimit()
    {
        IReadOnlyList<SearchHit> hits = LogicSearch.Find(Fixture.Line3, "(", limit: 3);
        Assert.Equal(3, hits.Count);
        Assert.Empty(LogicSearch.Find(Fixture.Line3, "   "));
    }

    [Fact]
    public async Task AResultOpensItsRungOnTheLogicTab()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        main.Search.Query = "commissioning";
        Assert.True(main.Search.SearchCommand.CanExecute(null));
        main.Search.SearchCommand.Execute(null);

        SearchHit hit = Assert.Single(main.Search.Results);
        Assert.StartsWith("1 place in 1 routine", main.Search.Summary, StringComparison.Ordinal);
        Assert.True(main.OpenSiteCommand.CanExecute(hit));
        main.OpenSiteCommand.Execute(hit);
        Assert.Equal(MainViewModel.LogicTab, main.SelectedTab);
        Assert.Equal(hit.Site.Routine, main.Routine!.Routine.QualifiedName);

        // Another project: last project's places are gone.
        await main.OpenAsync(Fixture.PathOf("RobotCell.L5X"));
        Assert.Empty(main.Search.Results);
    }
}
