using System.Diagnostics;
using System.Globalization;

namespace LogicControl.App.Diagnostics;

/// <summary>
/// How much memory LogicControl is using, in words - for the About box and the trace log, so
/// "it is slow on our big line" can be answered with a number, and a fix checked against one.
/// </summary>
public static class MemoryUse
{
    /// <summary>"Memory: 212 MB in use, 84 MB of it LogicControl's own objects."</summary>
    public static string Describe()
    {
        using Process self = Process.GetCurrentProcess();
        return Describe(self.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
    }

    internal static string Describe(long workingSet, long managed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Memory: {Megabytes(workingSet)} MB in use, {Megabytes(managed)} MB of it LogicControl's own objects.");

    private static long Megabytes(long bytes) => (bytes + 524_288) / 1_048_576;
}
