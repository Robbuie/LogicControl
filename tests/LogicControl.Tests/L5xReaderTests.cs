using System.Text;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

public class L5xReaderTests
{
    [Fact]
    public void ReadsTheControllerAndExportHeader()
    {
        PlcProject p = Fixture.Line3;

        Assert.Equal("Line3_PLC", p.Controller.Name);
        Assert.Equal("1756-L83E", p.Controller.ProcessorType);
        Assert.Equal("33.11", p.Controller.Revision);
        Assert.Equal("Line 3 packaging controller", p.Controller.Description);
        Assert.Equal("33.00", p.SoftwareRevision);
        Assert.Equal("Controller", p.TargetType);
    }

    [Fact]
    public void ReadsEveryModuleWithPortsAndConnections()
    {
        PlcProject p = Fixture.Line3;

        Assert.Equal(11, p.Modules.Count);

        ModuleInfo vfd = p.Modules.Single(m => m.Name == "VFD_101");
        Assert.Equal("192.168.10.31", vfd.IpAddress);
        Assert.Equal("ENBT_2", vfd.ParentModule);
        Assert.Equal(1.0, vfd.Connections.Single().RpiMs);
        Assert.Equal(24, vfd.Connections.Single().InputSize);
        Assert.True(vfd.Connections.Single().Unicast);

        ModuleInfo card = p.Modules.Single(m => m.Name == "Local_OB16");
        Assert.Equal(2, card.Slot);
        Assert.Null(card.IpAddress);

        ModuleInfo scale = p.Modules.Single(m => m.Name == "Scale_1");
        Assert.True(scale.IsGenericEthernet);
        Assert.Equal(102, scale.ConfigInstance);
        Assert.Equal("Disabled", scale.Keying);

        Assert.True(p.Modules.Single(m => m.Name == "Local").IsLocal);
        Assert.True(p.Modules.Single(m => m.Name == "Valve_Bank").Inhibited);
    }

    [Fact]
    public void DropsHiddenBoolHostsFromUdts()
    {
        DataTypeInfo udt = Fixture.Line3.DataTypes.Single();

        Assert.Equal(new[] { "Ready", "Fault", "CycleCount" }, udt.Members.Select(m => m.Name));
        Assert.Equal("BOOL", udt.Members[0].DataType);
        Assert.Equal("Completed cycles since power-up", udt.Members[2].Description);
    }

    [Fact]
    public void ReadsTagKindsAliasesAndMessages()
    {
        PlcProject p = Fixture.Line3;

        TagInfo alias = p.Tags.Single(t => t.Name == "E_Stop_OK");
        Assert.Equal(TagKind.Alias, alias.Kind);
        Assert.Equal("Local:1:I.Data.15", alias.AliasFor);

        TagInfo consumed = p.Tags.Single(t => t.Name == "Robot_Heartbeat");
        Assert.Equal(TagKind.Consumed, consumed.Kind);
        Assert.Equal("Robot_PLC", consumed.Consume!.Producer);
        Assert.Equal(50.0, consumed.Consume.RpiMs);

        Assert.Equal(2, p.Tags.Single(t => t.Name == "Line_Status").ProduceCount);

        MessageInfo msg = p.Tags.Single(t => t.Name == "Robot_Read").Message!;
        Assert.Equal("CIP Data Table Read", msg.MessageType);
        Assert.Equal("ENBT_2, 2, 192.168.10.5, 1, 0", msg.ConnectionPath);
        Assert.Equal("Robot_Status", msg.RemoteElement);
        Assert.Equal("Robot_Data", msg.LocalElement);
        Assert.True(msg.Connected);
        Assert.True(msg.CacheConnections);
    }

    [Fact]
    public void ReadsProgramsRoutinesAndTasks()
    {
        PlcProject p = Fixture.Line3;
        ProgramInfo main = p.Programs.Single(x => x.Name == "MainProgram");

        Assert.Equal("MainRoutine", main.MainRoutineName);
        Assert.Equal(2, main.Tags.Count);
        Assert.Equal("MainProgram", main.Tags[0].Scope);

        RoutineInfo motors = main.Routines.Single(r => r.Name == "Motors");
        Assert.Equal(RoutineLanguage.Ladder, motors.Language);
        Assert.Equal(7, motors.Rungs.Count);
        Assert.Equal("XIC(Line_Running)OTE(Conveyor_Run);", motors.Rungs[2].Text);
        Assert.Equal("Manual jog - added during commissioning", motors.Rungs[3].Comment);

        RoutineInfo calc = main.Routines.Single(r => r.Name == "Calc");
        Assert.Equal(RoutineLanguage.StructuredText, calc.Language);
        Assert.Equal(3, calc.StructuredText.Count);

        TaskInfo task = p.Tasks.Single();
        Assert.Equal("CONTINUOUS", task.Type);
        Assert.Equal(new[] { "MainProgram" }, task.ScheduledPrograms);
    }

    [Fact]
    public void ReadsAoiInterfaceAndCallOrder()
    {
        AoiInfo aoi = Fixture.Line3.AddOnInstructions.Single();

        Assert.Equal("Motor_Ctrl", aoi.Name);
        Assert.Equal("1.2", aoi.Revision);
        Assert.Equal(4, aoi.Parameters.Count);
        Assert.Equal(new[] { "Out", "Run_Cmd" }, aoi.CallParameters.Select(c => c.Name));
        Assert.Single(aoi.LocalTags);
        Assert.Single(aoi.Routines);
        Assert.True(aoi.Routines[0].OwnerIsAoi);
    }

    [Fact]
    public void RejectsAnAcdWithInstructions()
    {
        var ex = Assert.Throws<L5xFormatException>(() => L5xReader.Load(@"C:\Projects\Line3.ACD"));
        Assert.Contains("Save As", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsXmlThatIsNotL5x()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<Project><Controller/></Project>"));
        var ex = Assert.Throws<L5xFormatException>(() => L5xReader.Load(stream, "x.xml"));
        Assert.Contains("RSLogix5000Content", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedXmlWithALineNumber()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<RSLogix5000Content>\n<Controller>"));
        var ex = Assert.Throws<L5xFormatException>(() => L5xReader.Load(stream, "x.L5X"));
        Assert.Contains("line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToleratesAMinimalPartialExport()
    {
        const string xml = """
            <RSLogix5000Content SchemaRevision="1.0" TargetType="Routine" TargetName="R">
              <Controller Use="Context" Name="C">
                <Programs><Program Use="Context" Name="P"><Routines>
                  <Routine Use="Target" Name="R" Type="RLL"><RLLContent>
                    <Rung Number="0" Type="N"><Text><![CDATA[XIC(A)OTE(B);]]></Text></Rung>
                  </RLLContent></Routine>
                </Routines></Program></Programs>
              </Controller>
            </RSLogix5000Content>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        PlcProject p = L5xReader.Load(stream, "partial.L5X");

        Assert.Equal("Routine", p.TargetType);
        Assert.Empty(p.Modules);
        Assert.Single(p.Programs.Single().Routines.Single().Rungs);
    }

    [Theory]
    [InlineData("16#004c", 76)]
    [InlineData("2#0101", 5)]
    [InlineData("8#17", 15)]
    [InlineData("1_000", 1000)]
    [InlineData("42", 42)]
    [InlineData("nope", null)]
    public void ParsesLogixRadixIntegers(string raw, int? expected) =>
        Assert.Equal(expected, L5xReader.ParseInt(raw));
}
