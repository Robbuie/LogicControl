using System.Xml.Linq;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>Generic Ethernet module drafts: written, read back, checked and merged.</summary>
public class ModuleTests
{
    private static ModuleDraft Vision() => new()
    {
        Name = "Vision_1",
        Description = "Label inspection camera",
        ParentModule = "ENBT_2",
        ParentPortId = 2,
        IpAddress = "192.168.10.70",
        Format = ModuleFormats.Int,
        InputInstance = 101,
        InputSize = 10,
        OutputInstance = 100,
        OutputSize = 2,
        ConfigInstance = 102,
        RpiMs = 20,
    };

    [Fact]
    public void AModuleIsWrittenAsStudioExportsOneAndReadsBack()
    {
        XElement written = L5xWriter.Module(Vision());

        Assert.Equal("ETHERNET-MODULE", (string?)written.Attribute("CatalogNumber"));
        Assert.Equal("Disabled", (string?)written.Element("EKey")!.Attribute("State"));
        XElement connection = written.Descendants("Connection").Single();
        Assert.Equal("20000", (string?)connection.Attribute("RPI"));
        Assert.Equal("20", (string?)connection.Attribute("InputSize"));   // 10 INTs
        Assert.Equal("4", (string?)connection.Attribute("OutputSize"));   // 2 INTs

        var doc = new XDocument(new XElement("RSLogix5000Content",
            new XAttribute("TargetType", "Controller"),
            new XElement("Controller", new XAttribute("Name", "C"), new XElement("Modules", written))));
        ModuleInfo read = Assert.Single(L5xReader.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(L5xWriter.ToText(doc))), "m.L5X").Modules);

        Assert.True(read.IsGenericEthernet);
        Assert.Equal("192.168.10.70", read.IpAddress);
        Assert.Equal(20.0, read.Connections.Single().RpiMs);

        ModuleDraft again = DraftsFromProject.Module(read)!;
        Assert.Equal(ModuleFormats.Int, again.Format);
        Assert.Equal(10, again.InputSize);
        Assert.Equal(2, again.OutputSize);
        Assert.Equal(102, again.ConfigInstance);
    }

    [Fact]
    public void TheFixturesScaleBecomesADraft()
    {
        ModuleDraft scale = DraftsFromProject.Module(Fixture.Line3.Modules.Single(m => m.Name == "Scale_1"))!;
        Assert.Equal("ENBT_2", scale.ParentModule);
        Assert.Equal(ModuleFormats.Dint, scale.Format);
        Assert.Equal(5, scale.InputSize);
        Assert.Null(DraftsFromProject.Module(Fixture.Line3.Modules.Single(m => m.Name == "VFD_101")));
    }

    [Theory]
    [InlineData("ok", null)]
    [InlineData("bad-ip", "is not an IPv4 address")]
    [InlineData("no-parent", "is not in the I/O tree")]
    [InlineData("wrong-port", "has no Ethernet port 1")]
    [InlineData("taken-name", "already has a PowerFlex 525-EENET")]
    [InlineData("bad-format", "is not one of")]
    public void TheCheckerKnowsWhatStudioWouldRefuse(string variant, string? error)
    {
        ModuleDraft m = Vision();
        switch (variant)
        {
            case "bad-ip": m.IpAddress = "192.168.10"; break;
            case "no-parent": m.ParentModule = "ENBT_9"; break;
            case "wrong-port": m.ParentPortId = 1; break;
            case "taken-name": m.Name = "VFD_101"; break;
            case "bad-format": m.Format = "Data - LINT"; break;
        }

        List<DraftIssue> errors = DraftChecker.Check(new DevelopmentSet { Modules = [m] }, Fixture.Line3).Where(i => i.IsError).ToList();
        if (error is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(errors, e => e.Message.Contains(error, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AnAddressAlreadyInUseIsAWarning()
    {
        ModuleDraft m = Vision();
        m.IpAddress = "192.168.10.50";
        DraftIssue issue = Assert.Single(DraftChecker.Check(new DevelopmentSet { Modules = [m] }, Fixture.Line3));
        Assert.False(issue.IsError);
        Assert.Contains("Scale_1", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulesAreMergedIntoAProjectCopyUnderTheirParent()
    {
        string source = Fixture.PathOf("Line3.L5X");
        string output = Path.Combine(Path.GetTempPath(), $"lc-module-{Guid.NewGuid():N}.L5X");
        try
        {
            ModuleDraft scale = DraftsFromProject.Module(Fixture.Line3.Modules.Single(m => m.Name == "Scale_1"))!;
            scale.RpiMs = 50;
            MergeReport report = ProjectMerger.Merge(source, new DevelopmentSet { Modules = [Vision(), scale] }, output);

            Assert.Contains("Module Vision_1", report.Added);
            Assert.Contains("Module Scale_1", report.Replaced);

            ProjectAnalysis after = ProjectAnalysis.Open(output);
            HardwareNode enbt = after.Hardware.Single().Children.Single(c => c.Module.Name == "ENBT_2");
            Assert.Contains(enbt.Children, c => c.Module.Name == "Vision_1" && c.Module.IpAddress == "192.168.10.70");
            Assert.Equal(50.0, after.Project.Modules.Single(m => m.Name == "Scale_1").Connections.Single().RpiMs);
            Assert.Equal(Fixture.Line3.Modules.Count + 1, after.Project.Modules.Count);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AModuleNamedLikeANonGenericOneIsRefusedByTheMerge()
    {
        ModuleDraft m = Vision();
        m.Name = "VFD_101";
        string output = Path.Combine(Path.GetTempPath(), $"lc-module-{Guid.NewGuid():N}.L5X");
        Assert.Throws<InvalidOperationException>(() => ProjectMerger.Merge(Fixture.PathOf("Line3.L5X"), new DevelopmentSet { Modules = [m] }, output));
        Assert.False(File.Exists(output));
    }
}
