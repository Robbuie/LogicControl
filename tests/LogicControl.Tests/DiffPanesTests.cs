using LogicControl.App.ViewModels.Develop;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using Xunit;

namespace LogicControl.Tests;

/// <summary>A change shown as two panes: the older version on the left, the newer on the right.</summary>
public class DiffPanesTests
{
    private static ItemChange Change(DevelopmentSet before, DevelopmentSet after) => Assert.Single(SetDiff.Compare(before, after).Items);

    [Fact]
    public void EveryRungLandsOnItsOwnSide()
    {
        var before = new DevelopmentSet
        {
            Routines =
            [
                new RoutineDraft
                {
                    Name = "Motors",
                    Description = "Old",
                    Rungs = [new RungDraft("XIC(A)OTE(B);", "start"), new RungDraft("XIC(C)OTE(D);"), new RungDraft("NOP();")],
                },
            ],
        };
        var after = new DevelopmentSet
        {
            Routines =
            [
                new RoutineDraft
                {
                    Name = "Motors",
                    Description = "New",
                    Rungs = [new RungDraft("XIC(A)OTE(B);", "begin"), new RungDraft("XIC(C)XIO(E)OTE(D);"), new RungDraft("NOP();"), new RungDraft("XIC(F)OTE(G);")],
                },
            ],
        };

        IReadOnlyList<DiffRowViewModel> rows = DiffRows.Build(Change(before, after).Lines, draw: true, shapes: null);

        // The description: each side's own value.
        DiffRowViewModel description = rows[0];
        Assert.True(description.IsPair);
        Assert.Equal("Description: Old", description.Left!.Text);
        Assert.Equal("Description: New", description.Right!.Text);
        Assert.Equal("Changed", description.Left.Change);

        Assert.Contains(rows, r => r.IsHeading && r.Text == "Rungs");

        // An unchanged rung whose comment changed: the same rung both sides, each with its own comment.
        DiffRowViewModel first = Assert.Single(rows, r => r.IsPair && r.Left!.Rung == "XIC(A)OTE(B);");
        Assert.Equal("start", first.Left!.Comment);
        Assert.Equal("begin", first.Right!.Comment);
        Assert.Equal(DiffLineKind.Context, first.Right.Kind);
        Assert.Contains(rows, r => r.IsPair && r.Left!.Text.Contains("\"start\"", StringComparison.Ordinal) && r.Right!.Text.Contains("\"begin\"", StringComparison.Ordinal));

        // The edited rung: one row, old left, new right, drawn.
        DiffRowViewModel edited = Assert.Single(rows, r => r.IsPair && r.Left!.Kind == DiffLineKind.Removed);
        Assert.Equal("XIC(C)OTE(D);", edited.Left!.Rung);
        Assert.Equal("XIC(C)XIO(E)OTE(D);", edited.Right!.Rung);
        Assert.True(edited.Left.IsDrawn);
        Assert.Equal("Rung 1", edited.Right.Label);

        // The new rung: nothing on the left.
        DiffRowViewModel added = Assert.Single(rows, r => r.IsPair && r.Right!.Kind == DiffLineKind.Added && r.Left!.IsEmpty);
        Assert.Equal("XIC(F)OTE(G);", added.Right!.Rung);
        Assert.Equal("Rung 3", added.Right.Label);
    }

    [Fact]
    public void ContextRungsKeepTheirOwnNumberOnEachSide()
    {
        var before = new DevelopmentSet { Routines = [new RoutineDraft { Name = "R", Rungs = [new RungDraft("XIC(X)OTE(Y);"), new RungDraft("NOP();")] }] };
        var after = new DevelopmentSet { Routines = [new RoutineDraft { Name = "R", Rungs = [new RungDraft("XIC(X)OTE(Y);"), new RungDraft("XIC(N)OTE(M);"), new RungDraft("NOP();")] }] };

        IReadOnlyList<DiffRowViewModel> rows = DiffRows.Build(Change(before, after).Lines, draw: false, shapes: null);

        DiffRowViewModel nop = Assert.Single(rows, r => r.IsPair && r.Right!.Rung == "NOP();");
        Assert.Equal("Rung 1", nop.Left!.Label);
        Assert.Equal("Rung 2", nop.Right!.Label);
        Assert.True(nop.Left.IsRungText);
        Assert.False(nop.Left.IsDrawn);
    }

    [Fact]
    public void MembersAndNotesSplitOrSpanThePanes()
    {
        var before = new DevelopmentSet
        {
            DataTypes = [new UdtDraft { Name = "UDT_Motor", Members = [new MemberDraft { Name = "Run", DataType = "BOOL" }, new MemberDraft { Name = "Old", DataType = "DINT" }] }],
        };
        var after = new DevelopmentSet
        {
            DataTypes = [new UdtDraft { Name = "UDT_Motor", Members = [new MemberDraft { Name = "Run", DataType = "DINT" }] }],
        };

        IReadOnlyList<DiffRowViewModel> rows = DiffRows.Build(Change(before, after).Lines, draw: true, shapes: null);

        Assert.Contains(rows, r => r.IsPair && r.Left!.Text == "Run : BOOL" && r.Right!.Text == "Run : DINT");
        Assert.Contains(rows, r => r.IsPair && r.Left!.Text == "Old : DINT" && r.Left.Kind == DiffLineKind.Removed && r.Right!.IsEmpty);
        Assert.Equal("Description: x", DiffRows.Unprefixed("+ Description: x"));
        Assert.Equal("... 3 unchanged rungs", DiffRows.Unprefixed("   ... 3 unchanged rungs"));
    }
}
