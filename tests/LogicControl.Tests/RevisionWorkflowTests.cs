using LogicControl.App.ViewModels;
using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>
/// Editing the open project's routines in place, and the revision history behind it - driven the
/// way the window drives them.
/// </summary>
public class RevisionWorkflowTests
{
    private DateTime _now = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    private MainViewModel Opened(string routine = "MainProgram/Motors")
    {
        var main = new MainViewModel();
        main.Develop.Clock = () => _now;
        main.Load(Fixture.Line3Analysed);
        main.OpenSite(new Core.Analysis.FindingSite(routine, null));
        return main;
    }

    private void Later(int seconds = 5) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void EditingARoutineShowsTheChangesOnTheLogicTab()
    {
        MainViewModel main = Opened();
        Assert.False(main.Routine!.IsEdited);
        Assert.True(main.EditRoutineCommand.CanExecute(null));

        main.EditRungCommand.Execute(main.Routine.Lines[3]);
        Assert.Equal(MainViewModel.DevelopTab, main.SelectedTab);
        var editor = Assert.IsType<RoutineEditorViewModel>(main.Develop.Editor);
        Assert.Same(editor.Rungs.Items[3], editor.Rungs.Selected);

        // Change rung 3 and add one at the end.
        editor.Rungs.Items[3].Text = "XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);";
        editor.Rungs.Selected = editor.Rungs.Items[^1];
        editor.Rungs.AddCommand.Execute(null);

        RoutineViewModel shown = main.Routine!;
        Assert.True(shown.IsEdited);
        Assert.True(main.RoutineHasEdits);
        Assert.Equal("Changed", shown.Lines[3].Change);
        Assert.Contains("XIC(Manual_Mode)OTE(Conveyor_Run);", shown.Lines[3].ChangeNote, StringComparison.Ordinal);
        Assert.Equal("Added", shown.Lines[^1].Change);
        Assert.Equal(string.Empty, shown.Lines[0].Change);
        Assert.Equal("1 changed, 1 added", shown.EditSummary);

        // The original is a toggle away.
        main.ShowOriginal = true;
        Assert.False(main.Routine!.IsEdited);
        Assert.Equal(7, main.Routine.Lines.Count);
        Assert.True(main.RoutineHasEdits);
    }

    [Fact]
    public void ARemovedRungStaysVisibleStruckThrough()
    {
        MainViewModel main = Opened();
        main.EditRoutineCommand.Execute(null);
        var editor = (RoutineEditorViewModel)main.Develop.Editor!;
        editor.Rungs.Selected = editor.Rungs.Items[1];
        editor.Rungs.RemoveCommand.Execute(null);

        RoutineViewModel shown = main.Routine!;
        LogicLineViewModel removed = Assert.Single(shown.Lines, l => l.IsRemoved);
        Assert.Equal("MOV(Speed_Ref,VFD_101:O.FreqCommand);", removed.RungText);
        Assert.Equal(1, removed.ProjectLocation);
        Assert.Equal(-1, removed.Location);

        // A finding about project rung 3 still lands on that rung, now drawn as rung 2.
        main.OpenSite(new Core.Analysis.FindingSite("MainProgram/Motors", 3));
        LogicLineViewModel marked = Assert.Single(main.Routine!.Lines, l => l.IsHighlighted);
        Assert.Equal(2, marked.Location);
    }

    [Fact]
    public void TypingIsOneRevisionPerBurstAndPerDraft()
    {
        MainViewModel main = Opened();
        DevelopViewModel d = main.Develop;
        main.EditRoutineCommand.Execute(null);
        int start = d.History.Revisions.Count;
        Assert.Equal("Started editing routine MainProgram/Motors", d.History.Latest!.Label);

        var editor = (RoutineEditorViewModel)d.Editor!;
        foreach (string text in new[] { "XIC(A", "XIC(A)", "XIC(A)OTE(B);" })
        {
            Later(2);
            editor.Rungs.Items[0].Text = text;
        }

        // Still typing: nothing recorded yet. Moving to another draft records it.
        Assert.Equal(start, d.History.Revisions.Count);
        d.EditTagsCommand.Execute(null);
        Assert.Equal(start + 1, d.History.Revisions.Count);
        Assert.Equal("Edited routine MainProgram/Motors", d.History.Latest!.Label);
        Assert.Equal("Routine MainProgram/Motors: 1 rung changed", d.History.Latest.Summary);

        // A long session on one draft is split every minute.
        d.SelectedItem = d.Items.Single(i => i.Kind == DraftKind.Routine);
        editor = (RoutineEditorViewModel)d.Editor!;
        editor.Rungs.Items[1].Text = "NOP();";
        Later(90);
        editor.Rungs.Items[2].Text = "NOP();";
        Assert.Equal(start + 2, d.History.Revisions.Count);
    }

    [Fact]
    public void RevertingGoesBackToAnyStepAndCanItselfBeUndone()
    {
        MainViewModel main = Opened();
        DevelopViewModel d = main.Develop;
        main.EditRoutineCommand.Execute(null);
        RevisionRecord started = d.History.Latest!;

        var editor = (RoutineEditorViewModel)d.Editor!;
        editor.Rungs.Items[0].Text = "NOP();";
        d.NewDataTypeCommand.Execute(null);
        Assert.Equal("Added data type NewType", d.History.Latest!.Label);
        Assert.Equal("Edited routine MainProgram/Motors", d.History.Revisions[^2].Label);

        d.RevertTo(started);

        Assert.Empty(d.Set.DataTypes);
        Assert.Equal("XIC(Line_Running)XIC(VFD_101:I.Ready)OTE(VFD_101:O.Start);", d.Set.Routines.Single().Rungs[0].Text);
        Assert.Equal(started.Number, d.History.Latest!.RevertedTo);
        Assert.Equal(string.Empty, main.Routine!.EditSummary.Replace("no changes yet", string.Empty, StringComparison.Ordinal));

        // Undo the revert: go back to the revision before it.
        d.RevertTo(d.History.Revisions[^2]);
        Assert.Single(d.Set.DataTypes);
        Assert.Equal("NOP();", d.Set.Routines.Single().Rungs[0].Text);
    }

    [Fact]
    public void TheHistoryViewShowsEachStepAndRevertsFromIt()
    {
        MainViewModel main = Opened();
        DevelopViewModel d = main.Develop;
        main.EditRoutineCommand.Execute(null);
        ((RoutineEditorViewModel)d.Editor!).Rungs.Items[2].Text = "XIC(Line_Running)XIC(E_Stop_OK)OTE(Conveyor_Run);";

        d.ShowHistoryCommand.Execute(null);
        var history = Assert.IsType<HistoryViewModel>(d.Editor);

        // Newest first; the pending edit was recorded when the history opened.
        Assert.Equal("Edited routine MainProgram/Motors", history.Revisions[0].Label);
        Assert.True(history.Revisions[0].IsLatest);
        Assert.False(history.RevertCommand.CanExecute(null));

        ItemChangeViewModel change = Assert.Single(history.Changes);
        Assert.Equal("Routine MainProgram/Motors", change.Title);
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("E_Stop_OK", StringComparison.Ordinal));

        // Rungs are drawn as ladder by default: the old one and the new one, labelled.
        Assert.True(history.ShowLadder);
        DiffLineViewModel drawn = Assert.Single(change.Lines, l => l.IsDrawn && l.Kind == DiffLineKind.Added);
        Assert.Equal("XIC(Line_Running)XIC(E_Stop_OK)OTE(Conveyor_Run);", drawn.Rung);
        Assert.Equal("+ Rung 2 after", drawn.Label);
        Assert.Equal("Added", drawn.Change);
        Assert.Contains(change.Lines, l => l.IsDrawn && l.Kind == DiffLineKind.Removed && l.Label == "- Rung 2 before");
        Assert.All(change.Lines.Where(l => l.Kind == DiffLineKind.Heading), l => Assert.False(l.IsDrawn));
        history.ShowText = true;
        Assert.DoesNotContain(Assert.Single(history.Changes).Lines, l => l.IsDrawn);
        history.ShowLadder = true;

        // Against the project: the same routine, one rung changed.
        history.Mode = HistoryMode.Project;
        Assert.Equal("1 rung changed", Assert.Single(history.Changes).Summary);

        // Pick the revision before the edit and go back to it from the view.
        history.SelectedRevision = history.Revisions[1];
        Assert.Equal(HistoryMode.Step, history.Mode);
        history.Mode = HistoryMode.SinceThen;
        Assert.Equal("1 rung changed", Assert.Single(history.Changes).Summary);
        Assert.True(history.RevertCommand.CanExecute(null));
        history.RevertCommand.Execute(null);

        Assert.Same(history, d.Editor);
        Assert.StartsWith("Reverted to #", history.Revisions[0].Label, StringComparison.Ordinal);
        Assert.Equal("XIC(Line_Running)OTE(Conveyor_Run);", d.Set.Routines.Single().Rungs[2].Text);

        // Opening a change opens its draft at the rung.
        history.SelectedRevision = history.Revisions.Single(r => r.Label == "Edited routine MainProgram/Motors");
        history.OpenDraftCommand.Execute(history.Changes.Single());
        Assert.Equal(2, ((RoutineEditorViewModel)d.Editor!).Rungs.Items.IndexOf(((RoutineEditorViewModel)d.Editor!).Rungs.Selected!));
    }

    [Fact]
    public void DiscardingEditsPutsTheProjectBackOnTheLogicTab()
    {
        MainViewModel main = Opened();
        main.EditRoutineCommand.Execute(null);
        ((RoutineEditorViewModel)main.Develop.Editor!).Rungs.Items[0].Text = "NOP();";
        Assert.True(main.DiscardEditsCommand.CanExecute(null));

        main.DiscardEditsCommand.Execute(null);

        Assert.False(main.Routine!.IsEdited);
        Assert.False(main.RoutineHasEdits);
        Assert.Empty(main.Develop.Set.Routines);
        Assert.Equal("Discarded edits to routine MainProgram/Motors", main.Develop.History.Latest!.Label);
        Assert.Contains(main.Develop.History.Revisions, r => r.Label == "Edited routine MainProgram/Motors");
    }

    [Fact]
    public void ClearingKeepsTheHistoryAndSavingCarriesIt()
    {
        var d = new DevelopViewModel { Clock = () => _now };
        d.NewDataTypeCommand.Execute(null);
        d.ClearCommand.Execute(null);
        Assert.True(d.IsEmpty);
        Assert.Equal(new[] { "Started a development set", "Added data type NewType", "Cleared every draft" }, d.History.Revisions.Select(r => r.Label));

        string path = Path.Combine(Path.GetTempPath(), $"lc-history-{Guid.NewGuid():N}.lcdev");
        try
        {
            d.Save(path);
            var again = new DevelopViewModel();
            again.Open(path);
            // Opening adds nothing: the drafts are what the last revision holds.
            Assert.Equal(3, again.History.Revisions.Count);
            Assert.Equal("Cleared every draft", again.History.Latest!.Label);
            again.RevertTo(again.History.Revisions.Single(r => r.Label == "Added data type NewType"));
            Assert.Single(again.Set.DataTypes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClaudesChangesAreRevisionsOfTheirOwn()
    {
        MainViewModel main = Opened();
        DevelopViewModel d = main.Develop;
        main.EditRoutineCommand.Execute(null);
        ((RoutineEditorViewModel)d.Editor!).Rungs.Items[0].Text = "NOP();";

        // What the assistant does: commit the person's typing, change the set, report it.
        d.CommitPending();
        d.Set.Tags.Add(new TagDraft("Jam_Timer", "TIMER"));
        d.ChangedElsewhere(null, "Changed by the assistant", "1 tag(s) drafted: Jam_Timer.", RevisionAuthor.Claude);

        RevisionRecord claude = d.History.Latest!;
        Assert.Equal("Claude", claude.Author);
        Assert.Equal("1 tag(s) drafted: Jam_Timer.", claude.Label);
        Assert.Equal("Tag Jam_Timer added", claude.Summary);
        Assert.Equal("You", d.History.Revisions[^2].Author);
    }

    [Fact]
    public void AGenericModuleCanBeDraftedFromTheProject()
    {
        MainViewModel main = Opened();
        DevelopViewModel d = main.Develop;
        ModuleInfo scale = main.Analysis!.Project.Modules.Single(m => m.Name == "Scale_1");

        Assert.True(d.EditCopyOf(scale));
        var editor = Assert.IsType<ModuleEditorViewModel>(d.Editor);
        Assert.Equal("ENBT_2", editor.ParentModule);
        Assert.Contains("ENBT_2", d.ParentChoices);
        editor.RpiMs = "50";
        Assert.Contains("Scale_1:I.Data[0..4]", editor.TagsNote, StringComparison.Ordinal);

        Assert.False(d.EditCopyOf(main.Analysis.Project.Modules.Single(m => m.Name == "VFD_101")));

        d.NewModuleCommand.Execute(null);
        var fresh = Assert.IsType<ModuleEditorViewModel>(d.Editor);
        Assert.Equal("192.168.10.61", fresh.IpAddress);
        Assert.DoesNotContain(d.Issues, i => i.Level == Level.Error);
    }
}
