using LogicControl.Core.Analysis;
using LogicControl.Core.Logic;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>
/// The whole pipeline against Line3.L5X. Every deliberate fault in the fixture is marked there
/// with the rule it should trip; these tests hold each rule to exactly that.
/// </summary>
public class AnalysisTests
{
    private static ProjectAnalysis A => Fixture.Line3Analysed;

    [Fact]
    public void RebuildsTheIoTree()
    {
        HardwareNode root = Assert.Single(A.Hardware);
        Assert.Equal("Local", root.Module.Name);
        Assert.Equal("Slot 0", root.AddressText);

        Assert.Equal(new[] { "Local_IB16", "Local_OB16", "ENBT_2" }, root.Children.Select(c => c.Module.Name));

        HardwareNode enbt = root.Children[2];
        Assert.Equal(
            new[] { "Robot_PLC", "Rack1_AENT", "VFD_101", "VFD_102", "Scale_1", "Valve_Bank" },
            enbt.Children.Select(c => c.Module.Name));

        HardwareNode rack = enbt.Children[1];
        Assert.Equal("Local > ENBT_2 > Rack1_AENT", rack.Path);
        Assert.Equal("Rack1_IB8", Assert.Single(rack.Children).Module.Name);
        Assert.Equal(11, root.SelfAndDescendants().Count());
    }

    [Fact]
    public void MapsEveryKindOfCommunication()
    {
        IReadOnlyList<CommLink> c = A.Communications;

        Assert.Equal(7, c.Count(l => l.Kind == CommKind.Io));
        Assert.Equal(1, c.Count(l => l.Kind == CommKind.Produced));
        Assert.Equal(2, c.Count(l => l.Kind == CommKind.Consumed));
        Assert.Equal(2, c.Count(l => l.Kind == CommKind.Message));
        Assert.Equal(1, c.Count(l => l.Kind == CommKind.StatusRead));

        CommLink read = c.Single(l => l.Kind == CommKind.Message && l.Source!.StartsWith("Robot_Read", StringComparison.Ordinal));
        Assert.Equal("Robot_PLC, slot 0", read.To);
        Assert.Equal("192.168.10.5", read.Address);
        Assert.Contains("MainProgram/Comms Rung 0", read.Source, StringComparison.Ordinal);

        CommLink consumed = c.Single(l => l.Kind == CommKind.Consumed && l.From == "Robot_PLC");
        Assert.Equal("192.168.10.5", consumed.Address);
        Assert.Equal(50.0, consumed.RpiMs);
    }

    [Fact]
    public void CrossReferencesReadsWritesAndScopes()
    {
        CrossReference x = A.CrossReference;
        PlcProject p = A.Project;

        IReadOnlyList<TagUse> conveyor = x.UsesOf(p.Tags.Single(t => t.Name == "Conveyor_Run"));
        Assert.Equal(2, conveyor.Count(u => u.Access == TagAccess.Write));
        Assert.Equal(1, conveyor.Count(u => u.Access == TagAccess.Read));

        // Program scope wins over controller scope, and ST assignments are writes.
        TagInfo step = p.Programs[0].Tags.Single(t => t.Name == "Step");
        Assert.Contains(x.UsesOf(step), u => u.Instruction == "CPT" && u.Access == TagAccess.Write);

        TagInfo speed = p.Tags.Single(t => t.Name == "Speed_Ref");
        Assert.Contains(x.UsesOf(speed), u => u.IsStructuredText && u.Access == TagAccess.Write);

        // A fired read message writes its local element.
        TagInfo robotData = p.Tags.Single(t => t.Name == "Robot_Data");
        Assert.Contains(x.UsesOf(robotData), u => u.Instruction == "MSG Robot_Read" && u.Access == TagAccess.Write);

        // AOI internals resolve against the AOI, not the controller.
        Assert.True(x.Uses.ContainsKey("Motor_Ctrl.Sealed"));

        Assert.Equal(new[] { "Mystery_Bit" }, x.Unresolved.Keys);
        Assert.True(x.ModuleTagUses.ContainsKey("Local:1"));
        Assert.True(x.ModuleTagUses.ContainsKey("Local:2"));
        Assert.True(x.ModuleTagUses.ContainsKey("Rack1_AENT"));
        Assert.Contains("MainProgram/Calc", x.CalledRoutines);
    }

    [Theory]
    [InlineData("LC-HW-001", 1)]
    [InlineData("LC-HW-002", 1)]
    [InlineData("LC-HW-003", 3)]
    [InlineData("LC-HW-004", 1)]
    [InlineData("LC-HW-005", 1)]
    [InlineData("LC-COM-001", 1)]
    [InlineData("LC-COM-002", 1)]
    [InlineData("LC-MSG-001", 1)]
    [InlineData("LC-MSG-002", 1)]
    [InlineData("LC-LOG-001", 1)]
    [InlineData("LC-LOG-002", 1)]
    [InlineData("LC-LOG-003", 0)]
    [InlineData("LC-LOG-006", 1)]
    [InlineData("LC-LOG-007", 1)]
    [InlineData("LC-LOG-008", 1)]
    public void EachPlantedFaultTripsItsRuleExactly(string rule, int expected) =>
        Assert.Equal(expected, A.Findings.Count(f => f.Rule == rule));

    [Fact]
    public void FindingsNameTheRightThings()
    {
        Assert.Equal("192.168.10.31", A.Findings.Single(f => f.Rule == "LC-HW-001").Subject);
        Assert.Equal("Scale_1", A.Findings.Single(f => f.Rule == "LC-HW-005").Subject);
        Assert.Equal("Conveyor_Run", A.Findings.Single(f => f.Rule == "LC-LOG-001").Subject);
        Assert.Equal("MainProgram/OldLogic", A.Findings.Single(f => f.Rule == "LC-LOG-007").Subject);
        Assert.Contains("Unused_Count", A.Findings.Single(f => f.Rule == "LC-LOG-002").Message, StringComparison.Ordinal);
        Assert.Equal(16, A.Findings.Count);
        Assert.Equal(FindingSeverity.Error, A.Findings[0].Severity);
    }

    [Theory]
    [InlineData("ENBT_2, 2, 192.168.10.5, 1, 0", "Robot_PLC, slot 0", "192.168.10.5")]
    [InlineData("ENBT_2, 2, 10.0.0.9", "10.0.0.9", "10.0.0.9")]
    [InlineData("Robot_PLC", "Robot_PLC", null)]
    [InlineData("1, 3", "Backplane 1, 3", "1, 3")]
    [InlineData("", "(no path)", null)]
    public void ResolvesMessagePaths(string path, string name, string? address)
    {
        var byIp = new Dictionary<string, string> { ["192.168.10.5"] = "Robot_PLC" };
        MessageTarget target = CommsMap.ParsePath(path, byIp);

        Assert.Equal(name, target.Name);
        Assert.Equal(address, target.Address);
    }
}
