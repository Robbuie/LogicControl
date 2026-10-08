using System.IO;
using LogicControl.App.Composition;
using LogicControl.App.ViewModels;
using Xunit;

namespace LogicControl.Tests;

/// <summary>File &gt; Open recent: the list, its file, and what goes on it.</summary>
public class RecentFilesTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"lc-recent-{Guid.NewGuid():N}.json");

    [Fact]
    public void NewestFirstNoDuplicatesAndTenOfEachKind()
    {
        int tick = 0;
        var recent = new RecentFiles(null, _ => true, () => DateTimeOffset.UnixEpoch.AddMinutes(tick++));
        for (int i = 0; i < 12; i++)
        {
            recent.Add(Path.Combine(Path.GetTempPath(), $"E{i}.L5X"), RecentKind.Export);
        }

        recent.Add(Path.Combine(Path.GetTempPath(), "e3.l5x"), RecentKind.Export);
        recent.Add(Path.Combine(Path.GetTempPath(), "set.lcdev"), RecentKind.DevelopmentSet);

        Assert.Equal(RecentFiles.MaxPerKind, recent.Exports.Count);
        Assert.Equal("e3.l5x", recent.Exports[0].Name);
        Assert.Single(recent.Exports, f => f.Name.Equals("E3.L5X", StringComparison.OrdinalIgnoreCase));
        Assert.Single(recent.DevelopmentSets);
    }

    [Fact]
    public void SurvivesARestartAndJunk()
    {
        string file = TempFile();
        try
        {
            new RecentFiles(file).Add(Fixture.PathOf("Line3.L5X"), RecentKind.Export);
            Assert.Equal("Line3.L5X", new RecentFiles(file).Exports.Single().Name);

            File.WriteAllText(file, "{ not json");
            Assert.True(new RecentFiles(file).IsEmpty);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MissingFilesStayUntilRemoved()
    {
        var recent = new RecentFiles(null, p => !p.Contains("gone", StringComparison.Ordinal));
        recent.Add(Path.Combine(Path.GetTempPath(), "gone", "Old.L5X"), RecentKind.Export);
        recent.Add(Fixture.PathOf("Line3.L5X"), RecentKind.Export);

        Assert.True(recent.HasMissing);
        recent.RemoveMissing();
        Assert.False(recent.HasMissing);
        Assert.Single(recent.Exports);
    }

    [Fact]
    public async Task OpeningAnExportPutsItOnTheList()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));

        Assert.Equal("Line3.L5X", main.Recent.Exports.Single().Name);
    }

    [Fact]
    public async Task AFailedOpenDoesNot()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.L5X"));

        Assert.True(main.Recent.IsEmpty);
    }

    [Fact]
    public void SavingADevelopmentSetPutsItOnTheList()
    {
        string file = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}.lcdev");
        try
        {
            var main = new MainViewModel();
            main.Develop.Save(file);
            Assert.Equal(Path.GetFileName(file), main.Recent.DevelopmentSets.Single().Name);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
