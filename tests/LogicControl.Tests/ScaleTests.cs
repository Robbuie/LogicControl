using System.Diagnostics;
using System.Xml.Linq;
using LogicControl.App.Diagnostics;
using LogicControl.App.ViewModels;
using LogicControl.Core.Analysis;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;
using Xunit;

namespace LogicControl.Tests;

/// <summary>
/// A plant-sized export, built from Line3: its programs copied many times over, a routine of
/// thousands of rungs, tens of thousands of tags. Every number here is a budget a few times what
/// it takes today (measured: about 0.4 s and 17 MB held to open and analyse, 0.1 s to compare,
/// 0.06 s to check), so a change that makes opening, analysing or comparing a real plant several
/// times slower or heavier fails the build instead of reaching anybody.
/// </summary>
public class ScaleTests
{
    /// <summary>Line3 grown to <paramref name="copies"/> copies of each program, a routine of <paramref name="bigRungs"/> rungs and <paramref name="tags"/> extra controller tags.</summary>
    internal static XDocument BigExport(int copies, int bigRungs, int tags, Func<int, string>? rungText = null)
    {
        XDocument doc = XDocument.Load(Fixture.PathOf("Line3.L5X"));
        XElement controller = doc.Root!.Element("Controller")!;
        XElement programs = controller.Element("Programs")!;
        List<XElement> originals = [.. programs.Elements("Program")];

        for (int c = 1; c < copies; c++)
        {
            foreach (XElement original in originals)
            {
                var copy = new XElement(original);
                copy.SetAttributeValue("Name", $"{original.Attribute("Name")!.Value}_{c}");
                programs.Add(copy);
            }
        }

        XElement routine = new("Routine", new XAttribute("Name", "Big"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", Enumerable.Range(0, bigRungs).Select(i => new XElement("Rung",
                new XAttribute("Number", i), new XAttribute("Type", "N"),
                new XElement("Comment", new XCData($"Motor {i}: start, stop and run feedback")),
                new XElement("Text", new XCData(rungText?.Invoke(i) ?? $"XIC(Start_{i % 50})XIO(Stop_{i % 50})[OTE(Run_{i % 50}),TON(T_{i % 50},?,?)];"))))));
        originals[0].Element("Routines")!.Add(routine);
        originals[0].SetAttributeValue("MainRoutineName", "Big");

        XElement tagList = controller.Element("Tags")!;
        for (int t = 0; t < tags; t++)
        {
            tagList.Add(new XElement("Tag", new XAttribute("Name", $"Spare_{t}"), new XAttribute("TagType", "Base"),
                new XAttribute("DataType", "DINT"), new XAttribute("ExternalAccess", "Read/Write")));
        }

        return doc;
    }

    private static PlcProject Read(XDocument doc, string name)
    {
        using var stream = new MemoryStream();
        doc.Save(stream);
        stream.Position = 0;
        return L5xReader.Load(stream, name);
    }

    private static (T Result, TimeSpan Time, long Allocated) Measure<T>(Func<T> work)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        T result = work();
        clock.Stop();
        return (result, clock.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void APlantSizedExportOpensAndAnalysesWithinBudget()
    {
        XDocument doc = BigExport(copies: 150, bigRungs: 3000, tags: 20_000);

        long resting = GC.GetTotalMemory(forceFullCollection: true);
        (ProjectAnalysis analysis, TimeSpan time, long allocated) = Measure(() => ProjectAnalysis.Analyse(Read(doc, "Big.L5X")));
        long held = GC.GetTotalMemory(forceFullCollection: true) - resting;

        Assert.True(analysis.Project.AllRoutines.Sum(r => r.Rungs.Count) > 5000);
        Assert.True(analysis.Project.Tags.Count > 20_000);
        Assert.True(time < TimeSpan.FromSeconds(10), $"open and analyse took {time}");
        Assert.True(allocated < 1_000_000_000, $"open and analyse allocated {allocated / 1_000_000} MB");
        Assert.True(held < 200_000_000, $"the analysis holds {held / 1_000_000} MB");
        GC.KeepAlive(analysis);
    }

    [Fact]
    public void ComparingTwoPlantSizedExportsStaysWithinBudget()
    {
        PlcProject before = Read(BigExport(copies: 60, bigRungs: 3000, tags: 5000), "Before.L5X");
        PlcProject after = Read(BigExport(copies: 60, bigRungs: 3000, tags: 5000, rungText: i => i % 2 == 0 ? $"XIC(Changed_{i})OTE(Out_{i});" : $"XIC(Start_{i % 50})XIO(Stop_{i % 50})[OTE(Run_{i % 50}),TON(T_{i % 50},?,?)];"), "After.L5X");

        (ChangeSet changes, TimeSpan time, long allocated) = Measure(() => ProjectComparison.Compare(before, after));

        Assert.Contains(changes.Items, c => c.Name.EndsWith("Big", StringComparison.Ordinal));
        Assert.True(time < TimeSpan.FromSeconds(5), $"comparing took {time}");
        Assert.True(allocated < 500_000_000, $"comparing allocated {allocated / 1_000_000} MB");
    }

    [Fact]
    public void CheckingAnEditedBigRoutineStaysWithinBudget()
    {
        PlcProject project = Read(BigExport(copies: 20, bigRungs: 3000, tags: 5000), "Big.L5X");
        RoutineDraft edit = DraftsFromProject.Routine(project.AllRoutines.Single(r => r.Name == "Big"));
        edit.Rungs[1500].Text = "XIC(Start_1)OTE(Run_2);";
        var set = new DevelopmentSet { Routines = [edit] };

        (IReadOnlyList<DraftIssue> issues, TimeSpan time, long allocated) = Measure(() => DraftChecker.Check(set, project));

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.True(time < TimeSpan.FromSeconds(3), $"checking took {time}");
        Assert.True(allocated < 250_000_000, $"checking allocated {allocated / 1_000_000} MB");
    }

    [Fact]
    public async Task APlantSizedExportOpensInTheWindowWithinBudget()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lc-scale-{Guid.NewGuid():N}.L5X");
        BigExport(copies: 150, bigRungs: 3000, tags: 20_000).Save(path);
        try
        {
            long resting = GC.GetTotalMemory(forceFullCollection: true);
            var clock = Stopwatch.StartNew();
            var main = new MainViewModel();
            ProjectAnalysis? logged = null;
            main.ProjectLoaded += (_, a) => logged = a;
            await main.OpenAsync(path);
            Assert.True(main.HasProject, main.Error);
            clock.Stop();
            long held = GC.GetTotalMemory(forceFullCollection: true) - resting;

            Assert.Equal(3000, main.Routine!.Lines.Count);
            Assert.Same(main.Analysis, logged);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"opening took {clock.Elapsed}");
            Assert.True(held < 200_000_000, $"the open project holds {held / 1_000_000} MB");
            GC.KeepAlive(main);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MemoryIsDescribedInMegabytes() =>
        Assert.Equal("Memory: 212 MB in use, 84 MB of it LogicControl's own objects.", MemoryUse.Describe(212L * 1_048_576, 84L * 1_048_576 + 1000));
}
