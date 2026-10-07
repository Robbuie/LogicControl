using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Authoring;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The Develop tab and the ladder toggle, driven the way the window drives them - no window.</summary>
public class DevelopViewModelTests
{
    private static async Task<MainViewModel> OpenLine3()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        Assert.True(main.HasProject, main.Error);
        return main;
    }

    [Fact]
    public async Task LadderIsTheDefaultViewForALadderRoutine()
    {
        MainViewModel main = await OpenLine3();

        Assert.True(main.Routine!.IsLadder);
        Assert.True(main.LadderVisible);
        Assert.All(main.Routine.Lines, l => Assert.NotNull(l.RungText));

        main.ShowLadder = false;
        Assert.True(main.TextVisible);
        Assert.False(main.LadderVisible);
    }

    [Fact]
    public async Task ClickingAnOperandOpensItsTag()
    {
        MainViewModel main = await OpenLine3();
        string tag = main.Analysis!.Project.Tags.First().Name;

        main.ShowOperandCommand.Execute($"{tag}.Member[3]");

        Assert.Equal(MainViewModel.TagsTab, main.SelectedTab);
        Assert.Equal(tag, main.SelectedTag?.Name);
    }

    [Fact]
    public async Task TheDraftsFollowTheOpenProject()
    {
        MainViewModel main = await OpenLine3();
        DevelopViewModel d = main.Develop;

        Assert.Equal(main.Analysis!.Project.Controller.Name, d.ControllerName);
        Assert.True(d.IsEmpty);
        Assert.False(d.CanExport);

        main.EditRoutineCommand.Execute(null);

        Assert.Equal(MainViewModel.DevelopTab, main.SelectedTab);
        RoutineEditorViewModel editor = Assert.IsType<RoutineEditorViewModel>(d.Editor);
        Assert.Equal(main.Routine!.Lines.Count, editor.Rungs.Items.Count);
        Assert.True(d.CanMerge);
    }

    [Fact]
    public async Task ABrokenRungBlocksExportUntilItIsFixed()
    {
        MainViewModel main = await OpenLine3();
        DevelopViewModel d = main.Develop;
        main.EditRoutineCommand.Execute(null);
        var editor = (RoutineEditorViewModel)d.Editor!;
        RungEditorViewModel rung = editor.Rungs.Items[0];

        rung.Text = "XIC(A)[XIO(B),XIC(C)";
        Assert.Equal(Level.Error, rung.Level);
        Assert.False(d.CanExport);
        Assert.Contains(d.Issues, i => i.Level == Level.Error && i.Issue.Rung == 0);

        rung.Text = "NOP();";
        Assert.True(d.CanExport);
    }

    [Fact]
    public void ATemplateAddsItsTypeTagsAndRungsAndOpensTheRoutine()
    {
        var d = new DevelopViewModel();
        d.FromTemplateCommand.Execute(null);
        var generator = Assert.IsType<TemplateGeneratorViewModel>(d.Editor);

        generator.Template = LogicTemplates.Find("motor")!;
        generator.Instances = "M101-M103";
        generator.CreateProgram = true;
        generator.Program = "Motors";
        generator.GenerateCommand.Execute(null);

        Assert.Single(d.Set.DataTypes);
        Assert.Equal(3, d.Set.Tags.Count);
        Assert.Equal(12, d.Set.Routines.Single().Rungs.Count);
        Assert.IsType<RoutineEditorViewModel>(d.Editor);
        Assert.Equal(0, d.ErrorCount);
        Assert.True(d.CanExport);
        Assert.False(d.CanMerge);
    }

    [Theory]
    [InlineData("XIC(?)OTE(?);", "XIO(Stop)", "XIO(Stop);")]
    [InlineData("XIC(Start)OTE(Run);", "XIO(Stop)", "XIC(Start)XIO(Stop)OTE(Run);")]
    [InlineData("XIC(Start)OTE(Run);", "TON(?,1000,0)", "XIC(Start)OTE(Run)TON(?,1000,0);")]
    public void SnippetsGoWhereALadderEditorWouldPutThem(string rung, string snippet, string expected) =>
        Assert.Equal(expected, RungListViewModel.SnippetInsert(rung, snippet));

    [Fact]
    public void ASavedSetOpensAsItWasSaved()
    {
        var d = new DevelopViewModel();
        d.NewDataTypeCommand.Execute(null);
        d.NewRoutineCommand.Execute(null);
        string path = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}.lcdev");

        try
        {
            d.Save(path);
            Assert.False(d.IsDirty);

            var again = new DevelopViewModel();
            again.Open(path);

            Assert.Equal(d.Set.ToJson(), again.Set.ToJson());
            Assert.False(again.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
