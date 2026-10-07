using System.Diagnostics;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;

namespace LogicControl.Core.Analysis;

/// <summary>
/// One opened export and everything worked out from it. Immutable once built, so the UI can
/// hold it, bind to it and hand it to a background thread without locks.
/// </summary>
public sealed class ProjectAnalysis
{
    private ProjectAnalysis(
        PlcProject project,
        CrossReference xref,
        IReadOnlyList<HardwareNode> hardware,
        IReadOnlyList<CommLink> comms,
        IReadOnlyList<Finding> findings,
        TimeSpan elapsed)
    {
        Project = project;
        CrossReference = xref;
        Hardware = hardware;
        Communications = comms;
        Findings = findings;
        Elapsed = elapsed;
    }

    public PlcProject Project { get; }

    public CrossReference CrossReference { get; }

    /// <summary>The I/O tree's roots - normally one, the controller.</summary>
    public IReadOnlyList<HardwareNode> Hardware { get; }

    public IReadOnlyList<CommLink> Communications { get; }

    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>Read plus analysis, for the status bar - a 40 MB export should still say "under a second".</summary>
    public TimeSpan Elapsed { get; }

    public int RungCount => Project.AllRoutines.Sum(r => r.Rungs.Count);

    public int TagCount => Project.AllTags.Count();

    /// <summary>Reads <paramref name="path"/> and analyses it.</summary>
    /// <exception cref="L5xFormatException">The file is not an L5X export.</exception>
    public static ProjectAnalysis Open(string path)
    {
        var clock = Stopwatch.StartNew();
        PlcProject project = L5xReader.Load(path);
        return Analyse(project, clock);
    }

    /// <summary>Analyses an already-read project - the tests' entry point.</summary>
    public static ProjectAnalysis Analyse(PlcProject project) => Analyse(project, Stopwatch.StartNew());

    private static ProjectAnalysis Analyse(PlcProject project, Stopwatch clock)
    {
        ArgumentNullException.ThrowIfNull(project);

        CrossReference xref = CrossReference.Build(project);
        IReadOnlyList<HardwareNode> hardware = HardwareTree.Build(project);
        IReadOnlyList<CommLink> comms = CommsMap.Build(project, xref, hardware);
        IReadOnlyList<Finding> findings = FindingRules.Run(project, xref, hardware, comms);

        return new ProjectAnalysis(project, xref, hardware, comms, findings, clock.Elapsed);
    }
}
