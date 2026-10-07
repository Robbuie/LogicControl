using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The diff, the revision history, and comparing drafts with the project they change.</summary>
public class HistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static RoutineDraft Motors(params string[] rungs) => new()
    {
        Name = "Motors",
        Program = "MainProgram",
        Rungs = rungs.Select(r => new RungDraft(r)).ToList(),
    };

    [Fact]
    public void LineDiffFindsTheEditInTheMiddle()
    {
        IReadOnlyList<LineStep> steps = LineDiff.Compute(["a", "b", "c", "d"], ["a", "x", "c", "d", "e"]);
        Assert.Equal(3, steps.Count(s => s.Change == LineChange.Same));
        Assert.Equal(2, steps.Count(s => s.Change == LineChange.Added));
        Assert.Equal(1, steps.Count(s => s.Change == LineChange.Removed));
    }

    [Fact]
    public void LineDiffOfIdenticalListsIsAllSame()
    {
        Assert.All(LineDiff.Compute(["a", "b"], ["a", "b"]), s => Assert.Equal(LineChange.Same, s.Change));
        Assert.Empty(LineDiff.Compute([], []));
    }

    [Fact]
    public void AnEditedRungReadsAsChangedNotAddedAndRemoved()
    {
        var before = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);", "XIC(C)OTE(D);", "NOP();")] };
        var after = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);", "XIC(C)XIO(E)OTE(D);", "NOP();", "XIC(F)OTE(G);")] };

        ItemChange change = Assert.Single(SetDiff.Compare(before, after).Items);

        Assert.Equal(ItemChangeKind.Changed, change.Kind);
        Assert.Equal("Routine MainProgram/Motors", change.Title);
        Assert.Equal("1 rung changed, 1 rung added", change.Summary);
        Assert.Equal(new[] { 1, 3 }, change.TouchedRungs);
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "- Rung 1  XIC(C)OTE(D);");
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "+ Rung 1  XIC(C)XIO(E)OTE(D);");
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Added && l.Text.StartsWith("+ Rung 3", StringComparison.Ordinal));
    }

    [Fact]
    public void WhitespaceInARungIsNotAChangeButACommentIs()
    {
        var before = new DevelopmentSet { Routines = [Motors("XIC(A) OTE(B);")] };
        var after = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);")] };
        Assert.True(SetDiff.Compare(before, after).IsEmpty);

        after.Routines[0].Rungs[0].Comment = "Run the motor";
        ItemChange change = Assert.Single(SetDiff.Compare(before, after).Items);
        Assert.Equal("1 comment changed", change.Summary);
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Changed && l.Text.Contains("\"Run the motor\"", StringComparison.Ordinal));
    }

    [Fact]
    public void LongRoutinesShowOnlyTheRungsAroundAChange()
    {
        string[] rungs = Enumerable.Range(0, 50).Select(i => $"XIC(B{i})OTE(C{i});").ToArray();
        string[] edited = (string[])rungs.Clone();
        edited[25] = "XIC(B25)OTL(C25);";

        ItemChange change = Assert.Single(SetDiff.Compare(
            new DevelopmentSet { Routines = [Motors(rungs)] },
            new DevelopmentSet { Routines = [Motors(edited)] }).Items);

        Assert.Equal(2, change.Lines.Count(l => l.Kind == DiffLineKind.Context && l.Text.Contains("unchanged", StringComparison.Ordinal)));
        Assert.Contains(change.Lines, l => l.Text == "   ... 24 unchanged rungs");
        Assert.Contains(change.Lines, l => l.Kind == DiffLineKind.Context && l.Text.Contains("Rung 24", StringComparison.Ordinal));
        Assert.True(change.Lines.Count < 12);
    }

    [Fact]
    public void MembersTagsAndRemovalsAreReported()
    {
        var before = new DevelopmentSet
        {
            DataTypes = [new UdtDraft { Name = "Pump", Members = [new("Run", "BOOL"), new("Speed", "REAL"), new("Old", "DINT")] }],
            Tags = [new TagDraft("P1", "Pump"), new TagDraft("Gone", "DINT")],
        };
        var after = new DevelopmentSet
        {
            DataTypes = [new UdtDraft { Name = "pump", Members = [new("Run", "BOOL"), new("Speed", "DINT"), new("Fault", "BOOL")] }],
            Tags = [new TagDraft("P1", "Pump", "Pump one"), new TagDraft("P2", "Pump")],
        };

        ChangeSet changes = SetDiff.Compare(before, after);

        ItemChange udt = changes.Items.Single(i => i.What == "Data type");
        Assert.Equal(ItemChangeKind.Changed, udt.Kind);
        Assert.Equal("1 member added, 1 member removed, 1 member changed", udt.Summary);
        Assert.Contains(udt.Lines, l => l.Text == "~ Speed : REAL → Speed : DINT");

        Assert.Equal(ItemChangeKind.Changed, changes.Items.Single(i => i.Name == "P1").Kind);
        Assert.Equal(ItemChangeKind.Added, changes.Items.Single(i => i.Name == "P2").Kind);
        Assert.Equal(ItemChangeKind.Removed, changes.Items.Single(i => i.Name == "Gone").Kind);
        Assert.Contains("Data type pump", changes.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingKeepsOnlyRealChanges()
    {
        var set = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);")] };
        var history = new RevisionHistory(set);

        RevisionRecord first = history.Record(set, "Started", now: T0)!;
        Assert.Equal(1, first.Number);
        Assert.Null(history.Record(set, "Nothing", now: T0.AddMinutes(1)));

        set.Routines[0].Rungs.Add(new RungDraft("NOP();"));
        RevisionRecord second = history.Record(set, "Added a rung", RevisionAuthor.Claude, T0.AddMinutes(2))!;

        Assert.Equal(2, second.Number);
        Assert.Equal("Claude", second.Author);
        Assert.Equal("Routine MainProgram/Motors: 1 rung added", second.Summary);
        Assert.Same(set.History, history.Revisions);
        Assert.Equal(2, history.Revisions.Count);
    }

    [Fact]
    public void AnyRevisionCanBeRestoredAndRevertingIsAStep()
    {
        var set = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);")] };
        var history = new RevisionHistory(set);
        history.Record(set, "one", now: T0);
        set.Routines[0].Rungs[0].Text = "XIC(A)OTL(B);";
        history.Record(set, "two", now: T0);
        set.Routines.Clear();
        history.Record(set, "three", now: T0);

        // Back to one: a fresh copy, not the objects the history holds.
        DevelopmentSet back = RevisionHistory.Restore(history.Find(1)!);
        Assert.Equal("XIC(A)OTE(B);", back.Routines.Single().Rungs.Single().Text);
        Assert.Null(back.History);

        back.History = set.History;
        RevisionRecord revert = new RevisionHistory(back).Record(back, "Reverted to #1", now: T0, revertedTo: 1)!;
        Assert.Equal(4, revert.Number);
        Assert.Equal(1, revert.RevertedTo);

        // What each step did, and what reverting undid.
        Assert.Equal("Routine MainProgram/Motors: 1 rung changed", new RevisionHistory(back).ChangesIn(history.Find(2)!).Summary);
        Assert.Equal(ItemChangeKind.Removed, Assert.Single(new RevisionHistory(back).ChangesIn(history.Find(3)!).Items).Kind);
        Assert.True(RevisionHistory.Since(history.Find(1)!, back).IsEmpty);
    }

    [Fact]
    public void TheHistorySurvivesSavingAndReopening()
    {
        var set = new DevelopmentSet { Routines = [Motors("XIC(A)OTE(B);")] };
        var history = new RevisionHistory(set);
        history.Record(set, "one", now: T0);
        set.Routines[0].Rungs.Add(new RungDraft("NOP();", "spare"));
        history.Record(set, "two", now: T0.AddSeconds(30));

        DevelopmentSet reopened = DevelopmentSet.FromJson(set.ToJson());
        var again = new RevisionHistory(reopened);

        Assert.Equal(new[] { 1, 2 }, again.Revisions.Select(r => r.Number));
        Assert.Equal("two", again.Latest!.Label);
        Assert.Null(again.Record(reopened, "unchanged"));
        Assert.DoesNotContain("history", reopened.ToSnapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldestRevisionsGoPastTheLimit()
    {
        var set = new DevelopmentSet();
        var history = new RevisionHistory(set);
        for (int i = 0; i < RevisionHistory.Limit + 5; i++)
        {
            set.Tags = [new TagDraft($"T{i}", "DINT")];
            history.Record(set, $"step {i}", now: T0);
        }

        Assert.Equal(RevisionHistory.Limit, history.Revisions.Count);
        Assert.Equal(6, history.Revisions[0].Number);
        Assert.Equal(RevisionHistory.Limit + 5, history.Latest!.Number);
    }

    [Fact]
    public void ComparingWithTheProjectShowsWhatAnEditChanges()
    {
        PlcProject project = Fixture.Line3;
        RoutineInfo motors = project.Programs.Single(p => p.Name == "MainProgram").Routines.Single(r => r.Name == "Motors");
        RoutineDraft edited = DraftsFromProject.Routine(motors);
        edited.Rungs[3].Text = "XIC(Manual_Mode)XIO(Line_Running)OTE(Conveyor_Run);";
        edited.Rungs.RemoveAt(6);
        var set = new DevelopmentSet { Routines = [edited], Tags = [new TagDraft("New_Tag", "BOOL")] };

        DevelopmentSet before = DraftsFromProject.Counterparts(project, set);
        Assert.Single(before.Routines);
        Assert.Empty(before.Tags);

        ChangeSet changes = SetDiff.Compare(before, set);
        ItemChange routine = changes.Items.Single(i => i.What == "Routine");
        Assert.Equal(ItemChangeKind.Changed, routine.Kind);
        Assert.Equal("1 rung changed, 1 rung removed", routine.Summary);
        Assert.Equal(ItemChangeKind.Added, changes.Items.Single(i => i.What == "Tag").Kind);
    }
}
