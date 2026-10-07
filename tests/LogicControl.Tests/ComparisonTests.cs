using System.Text.Json;
using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Analysis;
using LogicControl.Core.Assistant;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>Comparing the open project with another export, and Claude reading the comparison.</summary>
public class ComparisonTests
{
    /// <summary>Line3 as a contractor might send it back: a rung changed, a tag added, a type gone, an RPI and a task rate moved, an ST line edited.</summary>
    private static PlcProject Edited()
    {
        PlcProject p = Fixture.Line3;
        ProgramInfo main = p.Programs.Single(x => x.Name == "MainProgram");
        RoutineInfo motors = main.Routines.Single(r => r.Name == "Motors");
        RoutineInfo calc = main.Routines.Single(r => r.Name == "Calc");

        RoutineInfo newMotors = motors with
        {
            Rungs = motors.Rungs.Select(r => r.Number == 3 ? r with { Text = "XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);" } : r).ToList(),
        };
        RoutineInfo newCalc = calc with { StructuredText = calc.StructuredText.Select(l => l.Replace("0.6", "0.65", StringComparison.Ordinal)).ToList() };
        ProgramInfo newMain = main with { Routines = main.Routines.Select(r => r == motors ? newMotors : r == calc ? newCalc : r).ToList() };

        return p with
        {
            SourcePath = "/plant/Line3_contractor.L5X",
            Programs = p.Programs.Select(x => x == main ? newMain : x).ToList(),
            Tags = [.. p.Tags, new TagInfo { Name = "Jam_Timer", DataType = "TIMER", Description = "Infeed jam" }],
            DataTypes = p.DataTypes.Where(d => d.Name != "UDT_Spare").ToList(),
            Modules = p.Modules.Select(m => m.Name == "Scale_1" ? m with { Connections = [m.Connections[0] with { RpiMicroseconds = 50_000 }] } : m).ToList(),
            Tasks = p.Tasks.Select(t => t.Name == "FastTask" ? t with { RateMs = 20 } : t).ToList(),
        };
    }

    [Fact]
    public void EveryKindOfDifferenceIsFound()
    {
        ChangeSet changes = ProjectComparison.Compare(Fixture.Line3, Edited());

        Assert.Equal(
            new[] { "Data type UDT_Spare", "Module Scale_1", "Task FastTask", "Routine MainProgram/Calc", "Routine MainProgram/Motors", "Tag Jam_Timer" },
            changes.Items.Select(i => i.Title));

        Assert.Equal(ItemChangeKind.Removed, changes.Items[0].Kind);
        Assert.Contains(changes.Items[1].Lines, l => l.Text == "~ RPI: 10 ms → 50 ms");
        Assert.Contains(changes.Items[2].Lines, l => l.Text == "~ Rate: 10 ms → 20 ms");

        ItemChange calc = changes.Items[3];
        Assert.Contains(calc.Lines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("0.65", StringComparison.Ordinal));

        ItemChange motors = changes.Items[4];
        Assert.Equal("1 rung changed", motors.Summary);
        Assert.Contains(motors.Lines, l => l.IsRung && l.Kind == DiffLineKind.Added && l.Rung == "XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);");
        Assert.Equal(ItemChangeKind.Added, changes.Items[5].Kind);

        // Swapped, every difference reads the other way.
        ChangeSet back = ProjectComparison.Compare(Edited(), Fixture.Line3);
        Assert.Equal(ItemChangeKind.Added, back.Items.Single(i => i.Name == "UDT_Spare").Kind);
        Assert.Equal(ItemChangeKind.Removed, back.Items.Single(i => i.Name == "Jam_Timer").Kind);
    }

    [Fact]
    public void ScopesNarrowTheComparison()
    {
        Assert.Equal(new[] { "MainProgram/Calc", "MainProgram/Motors" },
            ProjectComparison.Compare(Fixture.Line3, Edited(), "MainProgram").Items.Select(i => i.Name));
        Assert.Equal(new[] { "UDT_Spare", "Scale_1", "FastTask", "Jam_Timer" },
            ProjectComparison.Compare(Fixture.Line3, Edited(), ProjectComparison.ControllerScope).Items.Select(i => i.Name));
        Assert.Empty(ProjectComparison.Compare(Fixture.Line3, Fixture.Line3).Items);
        Assert.Contains("Fast", ProjectComparison.Scopes(Fixture.Line3, Edited()));
    }

    [Fact]
    public void TwoRoutinesOfDifferentNamesCanBeCompared()
    {
        RoutineInfo motors = Fixture.Line3.AllRoutines.Single(r => r.QualifiedName == "MainProgram/Motors");
        RoutineInfo fast = Fixture.Line3.AllRoutines.Single(r => r.QualifiedName == "Fast/MainRoutine");

        ItemChange change = ProjectComparison.CompareRoutines(motors, fast);

        Assert.Equal("MainProgram/Motors → Fast/MainRoutine", change.Name);
        Assert.Contains(change.Lines, l => l.IsRung);
        Assert.Equal("identical", ProjectComparison.CompareRoutines(motors, motors).Summary);
    }

    private sealed class Host(ProjectAnalysis? open, ProjectAnalysis? other) : IToolHost
    {
        public ProjectAnalysis? Analysis => open;

        public ProjectAnalysis? Comparison => other;

        public DevelopmentSet Drafts { get; } = new();

        public bool CanOpenProjects => false;

        public string? OpenProject(string path) => "no";

        public void DraftsChanged(string summary)
        {
        }
    }

    [Fact]
    public void ClaudeReadsTheComparisonThroughItsTools()
    {
        var tools = new LogicTools(new Host(Fixture.Line3Analysed, ProjectAnalysis.Analyse(Edited())));
        JsonElement none = JsonSerializer.SerializeToElement(new { });

        ToolResult summary = tools.Execute("compare_summary", none);
        Assert.False(summary.IsError);
        Assert.Contains("6 differences", summary.Text, StringComparison.Ordinal);
        Assert.Contains("- Routine MainProgram/Motors: changed - 1 rung changed", summary.Text, StringComparison.Ordinal);

        ToolResult item = tools.Execute("compare_item", JsonSerializer.SerializeToElement(new { kind = "Routine", name = "MainProgram/Motors" }));
        Assert.Contains("+ Rung 3", item.Text, StringComparison.Ordinal);
        Assert.Contains("XIO(Line_Running)", item.Text, StringComparison.Ordinal);

        ToolResult other = tools.Execute("read_other_routine", JsonSerializer.SerializeToElement(new { program = "MainProgram", routine = "Motors" }));
        Assert.Contains("   3: XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);", other.Text, StringComparison.Ordinal);

        ToolResult closed = new LogicTools(new Host(Fixture.Line3Analysed, null)).Execute("compare_summary", none);
        Assert.True(closed.IsError);
        Assert.Contains("Compare tab", closed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompareTabListsDifferencesAndTakesTheOtherVersion()
    {
        var main = new MainViewModel();
        main.Load(Fixture.Line3Analysed);
        CompareViewModel compare = main.Compare;
        Assert.False(compare.ReviewAllCommand.CanExecute(null));

        compare.Load(ProjectAnalysis.Analyse(Edited()));

        Assert.Equal("6 differences.", compare.Summary);
        Assert.Contains("Line3_contractor.L5X", compare.Direction, StringComparison.Ordinal);
        ItemChangeViewModel motors = compare.Changes.Single(c => c.Name == "MainProgram/Motors");
        Assert.Contains(motors.Lines, l => l.IsDrawn);

        // The type exists only in the open project: nothing to take.
        compare.SelectedChange = compare.Changes.Single(c => c.Name == "UDT_Spare");
        Assert.False(compare.TakeOtherCommand.CanExecute(null));
        Assert.Equal("Only in the open project", compare.TakeOtherText);

        compare.SelectedChange = motors;
        Assert.True(compare.TakeOtherCommand.CanExecute(null));
        compare.TakeOtherCommand.Execute(null);

        RoutineDraft taken = Assert.Single(main.Develop.Set.Routines);
        Assert.Equal("XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);", taken.Rungs[3].Text);
        Assert.Equal("Took Routine MainProgram/Motors from Line3_contractor.L5X", main.Develop.History.Latest!.Label);
        Assert.Equal(MainViewModel.DevelopTab, main.SelectedTab);

        // The Logic tab shows the open routine as edited, with the taken rung marked.
        main.OpenSite(new FindingSite("MainProgram/Motors", null));
        Assert.Equal("Changed", main.Routine!.Lines[3].Change);

        // Narrow, swap, close.
        compare.Scope = ProjectComparison.ControllerScope;
        Assert.Equal(4, compare.Changes.Count);
        compare.Swapped = true;
        Assert.Equal(ItemChangeKind.Added.ToString(), compare.Changes.Single(c => c.Name == "UDT_Spare").Kind);
        compare.Close();
        Assert.False(compare.HasOther);
        Assert.Empty(compare.Changes);
    }

    [Fact]
    public void TwoRoutinesOnTheTabAndAskingClaude()
    {
        var main = new MainViewModel();
        main.Load(Fixture.Line3Analysed);
        CompareViewModel compare = main.Compare;
        compare.Load(ProjectAnalysis.Analyse(Edited()));

        compare.ThisRoutine = "MainProgram/OldLogic";
        compare.OtherRoutine = "MainProgram/Motors";
        compare.ComparePairCommand.Execute(null);

        ItemChangeViewModel pair = Assert.Single(compare.Changes);
        Assert.Equal("MainProgram/OldLogic → MainProgram/Motors", pair.Name);
        compare.TakeOtherCommand.Execute(null);
        RoutineDraft draft = Assert.Single(main.Develop.Set.Routines);
        Assert.Equal("MainProgram/OldLogic", draft.QualifiedName);
        Assert.Equal(7, draft.Rungs.Count);

        compare.ShowAllCommand.Execute(null);
        Assert.Equal(6, compare.Changes.Count);

        Assert.Contains("compare_summary", compare.ReviewAllPrompt(), StringComparison.Ordinal);
        compare.SelectedChange = compare.Changes.Single(c => c.Name == "Scale_1");
        Assert.Contains("Module Scale_1", compare.ReviewSelectedPrompt(), StringComparison.Ordinal);
        Assert.Contains("Comparing the open project with Line3_contractor.L5X", main.Assistant.BuildContext(), StringComparison.Ordinal);
    }
}
