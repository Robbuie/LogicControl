using LogicControl.Core.Analysis;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The system view: Line3 and the robot cell opened together and joined.</summary>
public class PlantTests
{
    private static PlantModel Both() => PlantModel.Build([Fixture.Line3Analysed, Fixture.RobotCellAnalysed]);

    [Fact]
    public void OneProjectIsItsIoTreeAndItsMessages()
    {
        PlantModel plant = PlantModel.Build([Fixture.Line3Analysed]);

        PlantNode controller = Assert.Single(plant.Nodes, n => n.Kind == PlantNodeKind.Controller);
        Assert.Equal("Line3_PLC", controller.Name);
        Assert.Equal("1756-L83E", controller.Catalog);
        Assert.Equal(PlantNodeKind.Bridge, plant.Find("0:ENBT_2")!.Kind);
        Assert.Equal(PlantNodeKind.Device, plant.Find("0:VFD_101")!.Kind);
        Assert.Equal(PlantNodeKind.Device, plant.Find("0:Robot_PLC")!.Kind); // the robot cell is not open

        Assert.Contains(plant.Links, l => l.From == "0:ENBT_2" && l.To == "0:VFD_101" && l.Kind == PlantLinkKind.Io && l.Label == "1 ms");
        Assert.Contains(plant.Links, l => l.From == "0:Local" && l.To == "0:ENBT_2" && l.Kind == PlantLinkKind.Tree);
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Message && l.To == "0:Scale_1" && l.Label == "MSG Scale_Msg");
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Produced && l.From == "0:Robot_PLC" && l.Label == "Heartbeat → Robot_Heartbeat");
        Assert.Empty(plant.Findings);
    }

    [Fact]
    public void TwoProjectsAreJoinedThroughTheirTrees()
    {
        PlantModel plant = Both();

        Assert.Equal(2, plant.Nodes.Count(n => n.Kind == PlantNodeKind.Controller));
        PlantNode robotSeenFromLine3 = plant.Find("0:Robot_PLC")!;
        Assert.Equal(PlantNodeKind.Peer, robotSeenFromLine3.Kind);
        Assert.Equal(1, robotSeenFromLine3.PeerOf);
        Assert.Equal(0, plant.Find("1:Line3_PLC")!.PeerOf);
        Assert.Equal(PlantNodeKind.Bridge, plant.Find("1:Line3_ENBT")!.Kind);

        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.SameDevice && l.From == "0:Robot_PLC" && l.To == "1:Local");
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Produced && l.From == "1:Local" && l.To == "0:Local" && l.Label == "Heartbeat → Robot_Heartbeat");
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Produced && l.From == "0:Local" && l.To == "1:Local" && l.Label == "Line_Status → Line_Status_In");
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Message && l.From == "0:Local" && l.To == "1:Local" && l.Label == "MSG Robot_Read");
        Assert.Contains(plant.Links, l => l.Kind == PlantLinkKind.Message && l.From == "1:Local" && l.To == "0:Local" && l.Label == "MSG Cmd_Write");
    }

    [Theory]
    [InlineData("LC-PLT-001", "Robot_PLC.Line_State_In")]
    [InlineData("LC-PLT-002", "Line3_PLC.Robot_Heartbeat")]
    [InlineData("LC-PLT-003", "Line3_PLC.Robot_Heartbeat")]
    [InlineData("LC-PLT-005", "Robot_PLC.Cmd_Write")]
    [InlineData("LC-PLT-006", "192.168.10.50")]
    public void EachPlantedPlantFaultTripsItsRule(string rule, string subject)
    {
        Finding finding = Assert.Single(Both().Findings, f => f.Rule == rule);
        Assert.Equal(subject, finding.Subject);
    }

    [Fact]
    public void OnlyThePlantedFaultsAreFound()
    {
        Assert.Equal(5, Both().Findings.Count);
        Assert.Equal(FindingSeverity.Error, Both().Findings[0].Severity);
    }

    [Fact]
    public void AProducedTagWithTooManyConsumersIsFound()
    {
        ProjectAnalysis line3 = Fixture.Line3Analysed;
        PlcProject oneConsumer = line3.Project with
        {
            Tags = line3.Project.Tags.Select(t => t.Name == "Line_Status" ? t with { ProduceCount = 0 } : t).ToList(),
        };

        PlantModel plant = PlantModel.Build([ProjectAnalysis.Analyse(oneConsumer), Fixture.RobotCellAnalysed]);

        Finding overbooked = Assert.Single(plant.Findings, f => f.Rule == "LC-PLT-004");
        Assert.Equal("Line3_PLC.Line_Status", overbooked.Subject);
        Assert.Contains("Robot_PLC.Line_Status_In", overbooked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLayoutStacksTheTreesAndRoutesEveryLink()
    {
        PlantModel plant = Both();
        PlantLayout layout = PlantLayout.Build(plant);

        Assert.Equal(plant.Nodes.Count, layout.Boxes.Count);
        Assert.Equal(plant.Links.Count, layout.Routes.Count);

        PlantBox line3 = layout.Boxes.Single(b => b.Node.Id == "0:Local");
        PlantBox robot = layout.Boxes.Single(b => b.Node.Id == "1:Local");
        PlantBox enbt = layout.Boxes.Single(b => b.Node.Id == "0:ENBT_2");
        Assert.Equal(line3.X, robot.X);
        Assert.True(robot.Y > layout.Boxes.Where(b => b.Node.Project == 0).Max(b => b.Y), "the robot cell is below Line3's whole tree");
        Assert.Equal(line3.X + PlantLayout.BoxWidth + PlantLayout.ColumnGap, enbt.X);

        // No two boxes overlap.
        foreach (PlantBox a in layout.Boxes)
        {
            Assert.DoesNotContain(layout.Boxes, b => !ReferenceEquals(a, b)
                && a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height);
        }

        // A parent sits between its first and last child.
        List<PlantBox> kids = layout.Boxes.Where(b => b.Node.ParentId == "0:ENBT_2").ToList();
        Assert.InRange(enbt.Y, kids.Min(k => k.Y), kids.Max(k => k.Y));

        Assert.All(layout.Routes.Where(r => r.Link.Kind is PlantLinkKind.Io or PlantLinkKind.Tree), r => Assert.False(r.IsCurve));
        Assert.All(layout.Routes.Where(r => r.Link.Kind is PlantLinkKind.Message or PlantLinkKind.Produced), r => Assert.Equal(4, r.Points.Count));
        Assert.Same(robot, layout.HitTest(robot.X + 5, robot.Y + 5));
        Assert.Null(layout.HitTest(-10, -10));
    }
}
