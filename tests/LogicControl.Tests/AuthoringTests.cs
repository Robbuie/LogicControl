using System.Xml.Linq;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

public class AuthoringTests
{
    private static readonly DateTime Stamp = new(2026, 10, 6, 23, 0, 0, DateTimeKind.Utc);

    private static PlcProject Read(XDocument document) =>
        L5xReader.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(L5xWriter.ToText(document))), "written.L5X");

    private static DevelopmentSet MotorSet(bool programTags = false) =>
        LogicTemplates.Find("motor")!.Generate(new TemplateRequest
        {
            Instances = ["M101", "M102"],
            TypeName = "Motor",
            Program = "Line3",
            RoutineName = "Motors",
            ProgramScopeTags = programTags,
        });

    [Fact]
    public void AUdtRoundTripsWithItsBoolsPackedIntoHiddenHosts()
    {
        var udt = new UdtDraft
        {
            Name = "Pump",
            Description = "A pump",
            Members =
            [
                new("Run", "BOOL", "Run it"),
                new("Fault", "BOOL"),
                new("Speed", "REAL", "Speed in Hz"),
                new("Ready", "BOOL"),
                new("Flags", "BOOL", dimension: 32),
                new("Delay", "TIMER"),
                new("Counts", "dint", dimension: 4),
            ],
        };

        XDocument doc = L5xWriter.ExportDataType(new DevelopmentSet { DataTypes = [udt] }, udt, Stamp);
        XElement written = doc.Descendants("DataType").Single();

        // Run and Fault share one host; Speed ends it; Ready starts a second.
        List<XElement> hosts = written.Descendants("Member").Where(m => (string?)m.Attribute("Hidden") == "true").ToList();
        Assert.Equal(2, hosts.Count);
        Assert.Equal("ZZZZZZZZZZPump0", (string?)hosts[0].Attribute("Name"));
        XElement fault = written.Descendants("Member").Single(m => (string?)m.Attribute("Name") == "Fault");
        Assert.Equal("BIT", (string?)fault.Attribute("DataType"));
        Assert.Equal("1", (string?)fault.Attribute("BitNumber"));
        Assert.Equal("ZZZZZZZZZZPump0", (string?)fault.Attribute("Target"));

        DataTypeInfo read = Assert.Single(Read(doc).DataTypes);
        Assert.Equal("Pump", read.Name);
        Assert.Equal("A pump", read.Description);
        Assert.Equal(
            new[] { "Run:BOOL:0", "Fault:BOOL:0", "Speed:REAL:0", "Ready:BOOL:0", "Flags:BOOL:32", "Delay:TIMER:0", "Counts:DINT:4" },
            read.Members.Select(m => $"{m.Name}:{m.DataType}:{m.Dimension}"));
        Assert.Equal("Speed in Hz", read.Members[2].Description);
    }

    [Fact]
    public void ARoutineExportCarriesTheTypesAndTagsItNeedsAsContext()
    {
        DevelopmentSet set = MotorSet();
        XDocument doc = L5xWriter.ExportRoutine(set, set.Routines[0], Stamp);
        XElement root = doc.Root!;

        Assert.Equal("Routine", (string?)root.Attribute("TargetType"));
        Assert.Equal("RLL", (string?)root.Attribute("TargetSubType"));
        Assert.Equal("Motors", (string?)root.Attribute("TargetName"));
        Assert.Equal("Target", (string?)doc.Descendants("Routine").Single().Attribute("Use"));
        Assert.Equal("Context", (string?)doc.Descendants("DataType").Single().Attribute("Use"));

        PlcProject read = Read(doc);
        Assert.Equal(new[] { "M101", "M102" }, read.Tags.Select(t => t.Name));
        Assert.All(read.Tags, t => Assert.Equal("Motor", t.DataType));

        RoutineInfo routine = read.Programs.Single(p => p.Name == "Line3").Routines.Single();
        Assert.Equal(8, routine.Rungs.Count);
        Assert.Equal("[XIC(M101.Cmd_Start),XIC(M101.Run)]XIO(M101.Cmd_Stop)XIO(M101.Fault)OTE(M101.Run);", routine.Rungs[0].Text);
        Assert.StartsWith("M101 - run", routine.Rungs[0].Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedTemplatesPassTheChecksWhenTheirProgramExists()
    {
        foreach (LogicTemplate template in LogicTemplates.All)
        {
            DevelopmentSet set = template.Generate(new TemplateRequest
            {
                Instances = ["X1", "X2"],
                TypeName = template.DefaultTypeName,
                Program = "Gen",
                RoutineName = "Logic_" + template.DefaultTypeName,
                CreateProgram = true,
            });

            List<DraftIssue> errors = DraftChecker.Check(set).Where(i => i.IsError).ToList();
            Assert.True(errors.Count == 0, $"{template.Title}: {string.Join(" | ", errors.Select(e => e.Location + ": " + e.Message))}");

            List<DraftIssue> warnings = DraftChecker.Check(set).Where(i => !i.IsError).ToList();
            Assert.True(warnings.Count == 0, $"{template.Title}: {string.Join(" | ", warnings.Select(e => e.Location + ": " + e.Message))}");
        }
    }

    [Fact]
    public void AnAoiRoundTripsWithItsSystemParametersAndCallShape()
    {
        DevelopmentSet set = LogicTemplates.Find("motor-aoi")!.Generate(new TemplateRequest
        {
            Instances = ["M201"],
            TypeName = "Motor_Ctl",
            Program = "Line3",
            RoutineName = "Motors",
        });

        AoiDraft aoi = set.AddOnInstructions.Single();
        AoiInfo read = Assert.Single(Read(L5xWriter.ExportAoi(set, aoi, Stamp)).AddOnInstructions);

        Assert.Equal("Motor_Ctl", read.Name);
        Assert.Equal(new[] { "EnableIn", "EnableOut" }, read.Parameters.Take(2).Select(p => p.Name));
        Assert.Equal(new[] { "Cmd_Start", "Cmd_Stop", "Run_FB", "Overload" }, read.CallParameters.Select(p => p.Name));
        Assert.Equal("FailToStart", Assert.Single(read.LocalTags).Name);
        Assert.Equal(5, read.Routines.Single().Rungs.Count);
        Assert.Equal("Logic", read.Routines.Single().Name);
    }

    [Fact]
    public void AProgramExportHoldsItsTagsAndRoutines()
    {
        DevelopmentSet set = LogicTemplates.Find("valve")!.Generate(new TemplateRequest
        {
            Instances = ["XV1"],
            TypeName = "Valve",
            Program = "Valves",
            RoutineName = "Main",
            ProgramScopeTags = true,
            CreateProgram = true,
        });

        PlcProject read = Read(L5xWriter.ExportProgram(set, set.Programs[0], Stamp));
        ProgramInfo program = Assert.Single(read.Programs);

        Assert.Equal("Main", program.MainRoutineName);
        Assert.Equal("XV1", Assert.Single(program.Tags).Name);
        Assert.Equal(6, Assert.Single(program.Routines).Rungs.Count);
        Assert.Equal("Valve", Assert.Single(read.DataTypes).Name);
    }

    [Fact]
    public void ExportAllNumbersFilesInImportOrder()
    {
        DevelopmentSet set = MotorSet();
        set.Merge(LogicTemplates.Find("motor-aoi")!.Generate(new TemplateRequest { Instances = ["M9"], TypeName = "Motor_Ctl", Program = "Line3", RoutineName = "AoiMotors" }));

        IReadOnlyList<(string FileName, XDocument Document)> files = L5xWriter.ExportAll(set, Stamp);

        Assert.Equal(
            new[] { "01_DataType_Motor.L5X", "02_AOI_Motor_Ctl.L5X", "03_Routine_Line3_Motors.L5X", "04_Routine_Line3_AoiMotors.L5X" },
            files.Select(f => f.FileName));
    }

    [Fact]
    public void ChecksCatchTheMistakesStudioWouldRefuse()
    {
        var set = new DevelopmentSet
        {
            DataTypes =
            [
                new UdtDraft { Name = "Bad__Name", Members = [new("x", "DINT")] },
                new UdtDraft { Name = "Loop", Members = [new("Self", "Loop")] },
                new UdtDraft { Name = "Bits", Members = [new("Flags", "BOOL", dimension: 10), new("A", "NOPE")] },
            ],
            Tags = [new TagDraft("T1", "TIMER"), new TagDraft("Speed", "REAL")],
            Programs = [new ProgramDraft { Name = "P1" }],
            Routines =
            [
                new RoutineDraft
                {
                    Name = "R1",
                    Program = "P1",
                    Rungs =
                    [
                        new("XIC(A)[XIO(B),XIC(C)OTE(D);"),
                        new("TON(T1,1000);"),
                        new("XIC(T1.DONE)OTE(Speed.40);"),
                        new("XIC(Missing)OTE(?);"),
                        new("XIO(T1.DN)TON(T1,?,?);"),
                    ],
                },
            ],
        };

        List<DraftIssue> issues = DraftChecker.Check(set).ToList();
        string all = string.Join("\n", issues.Select(i => $"{i.Severity} {i.Location}: {i.Message}"));

        Assert.Contains("two underscores", all, StringComparison.Ordinal);
        Assert.Contains("contain itself", all, StringComparison.Ordinal);
        Assert.Contains("multiple of 32", all, StringComparison.Ordinal);
        Assert.Contains("'NOPE' is not", all, StringComparison.Ordinal);
        Assert.Contains("never closed", all, StringComparison.Ordinal);
        Assert.Contains("TON at position 1 takes 3 operands", all, StringComparison.Ordinal);
        Assert.Contains("TIMER has no member 'DONE'", all, StringComparison.Ordinal);
        Assert.Contains("Warning Routine P1/R1, rung 3: XIC at position 1: tag 'Missing' is not declared", all, StringComparison.Ordinal);
        Assert.Contains("still '?'", all, StringComparison.Ordinal);
        Assert.Contains(issues, i => i.IsError && i.Rung == 1);

        // Studio 5000's own export writes TON(T1,?,?): a '?' preset is not a placeholder.
        Assert.DoesNotContain(issues, i => i.Rung == 4);
    }

    [Fact]
    public void ARungCanSeeTheOpenProjectsTagsAndAois()
    {
        var set = new DevelopmentSet
        {
            Routines = [new RoutineDraft { Name = "New", Program = "Conveyors", Rungs = [new("XIC(Start_PB)OTE(Conveyor_Run);")] }],
        };

        // Line3 declares these; nothing is drafted, so the only source is the project.
        PlcProject project = Fixture.Line3;
        string program = project.Programs.First().Name;
        string tag = project.Tags.First(t => t.DataType == "BOOL" || t.DataType == "DINT").Name;
        set.Routines[0].Program = program;
        set.Routines[0].Rungs[0].Text = $"XIC({tag})NOP();";

        Assert.DoesNotContain(DraftChecker.Check(set, project), i => i.Message.Contains("not declared", StringComparison.Ordinal));
    }

    [Fact]
    public void MergesIntoAWholeProjectAndKeepsEverythingElse()
    {
        string source = Fixture.PathOf("Line3.L5X");
        PlcProject before = Fixture.Line3;
        string program = before.Programs.First().Name;

        DevelopmentSet set = LogicTemplates.Find("alarm")!.Generate(new TemplateRequest
        {
            Instances = ["AL1"],
            TypeName = "Alarm",
            Program = program,
            RoutineName = "Alarms",
        });

        string output = Path.Combine(Path.GetTempPath(), $"lc-merge-{Guid.NewGuid():N}.L5X");
        try
        {
            MergeReport report = ProjectMerger.Merge(source, set, output, Stamp);
            PlcProject after = L5xReader.Load(output);

            Assert.Contains("Data type Alarm", report.Added);
            Assert.Equal(before.Modules.Count, after.Modules.Count);
            Assert.Equal(before.Tags.Count + 1, after.Tags.Count);
            Assert.Equal(before.DataTypes.Count + 1, after.DataTypes.Count);
            Assert.Contains(after.Programs.Single(p => p.Name == program).Routines, r => r.Name == "Alarms" && r.Rungs.Count == 5);

            // The analysis still runs on the merged file, and the new routine is reached by nobody yet.
            ProjectAnalysis analysis = ProjectAnalysis.Analyse(after);
            Assert.Contains(analysis.Findings, f => f.Subject.Contains("Alarms", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void RefusesToOverwriteTheFileItRead()
    {
        string source = Fixture.PathOf("Line3.L5X");
        Assert.Throws<InvalidOperationException>(() => ProjectMerger.Merge(source, new DevelopmentSet(), source));
    }

    [Theory]
    [InlineData("XIC( A ) OTE(B)", "XIC(A)OTE(B);")]
    [InlineData("XIC(A)\r\nOTE(B);;", "XIC(A)OTE(B);")]
    [InlineData("CPT(Out, (A + B) * 2)", "CPT(Out,(A + B) * 2);")]
    [InlineData("[XIC(A) , XIC(B)] OTE(C)", "[XIC(A),XIC(B)]OTE(C);")]
    [InlineData("", ";")]
    public void NormalisesRungTextTheWayStudioStoresIt(string text, string expected) =>
        Assert.Equal(expected, L5xWriter.NormaliseRung(text));

    [Fact]
    public void ReadsPastedDeclarationsInTheShapesPeopleHaveThem()
    {
        (List<MemberDraft> members, List<string> problems) = DeclarationText.ParseMembers(
            "Name\tType\tDescription\n"
            + "Speed : real // Commanded speed\n"
            + "Counts DINT[4] Per lane counts\n"
            + "Flags,BOOL[32],Status flags\n"
            + "// a comment\n"
            + "Broken\n");

        Assert.Equal(new[] { "Speed:REAL:0:Commanded speed", "Counts:DINT:4:Per lane counts", "Flags:BOOL:32:Status flags" },
            members.Select(m => $"{m.Name}:{m.DataType}:{m.Dimension}:{m.Description}"));
        Assert.Equal("Line 6: expected a name and a type, found 'Broken'.", Assert.Single(problems));
    }

    [Theory]
    [InlineData("M101, M102 M103", new[] { "M101", "M102", "M103" })]
    [InlineData("P07-P10", new[] { "P07", "P08", "P09", "P10" })]
    [InlineData("XV1-3", new[] { "XV1", "XV2", "XV3" })]
    [InlineData("M1,m1", new[] { "M1" })]
    public void ExpandsInstanceLists(string text, string[] expected) =>
        Assert.Equal(expected, TemplateRequest.ParseInstances(text));

    [Fact]
    public void ADevelopmentSetSurvivesSavingAndLoading()
    {
        DevelopmentSet set = MotorSet(programTags: true);
        DevelopmentSet back = DevelopmentSet.FromJson(set.ToJson());

        Assert.Equal(set.ToJson(), back.ToJson());
        Assert.Equal("Line3", back.Tags[0].Program);
        Assert.Throws<InvalidDataException>(() => DevelopmentSet.FromJson("{ not json"));
    }
}
