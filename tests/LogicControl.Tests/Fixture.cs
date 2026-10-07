using LogicControl.Core.Analysis;
using LogicControl.Core.L5x;
using LogicControl.Core.Model;

namespace LogicControl.Tests;

/// <summary>Opens the hand-written fixtures. Each is read once per test run.</summary>
internal static class Fixture
{
    private static readonly Lazy<PlcProject> Line3Project = new(() => L5xReader.Load(PathOf("Line3.L5X")));

    private static readonly Lazy<ProjectAnalysis> Line3Analysis = new(() => ProjectAnalysis.Analyse(Line3Project.Value));

    private static readonly Lazy<ProjectAnalysis> RobotAnalysis = new(() => ProjectAnalysis.Open(PathOf("RobotCell.L5X")));

    public static PlcProject Line3 => Line3Project.Value;

    /// <summary>The robot cell controller Line3 talks to - opened beside it for the system view.</summary>
    public static ProjectAnalysis RobotCellAnalysed => RobotAnalysis.Value;

    public static ProjectAnalysis Line3Analysed => Line3Analysis.Value;

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
