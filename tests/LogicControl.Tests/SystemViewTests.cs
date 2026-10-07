using LogicControl.App.ViewModels;
using LogicControl.Core.Analysis;
using Xunit;

namespace LogicControl.Tests;

/// <summary>The System tab, driven without a window.</summary>
public class SystemViewTests
{
    [Fact]
    public async Task TheOpenProjectIsDrawnAndAnotherJoinsIt()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        SystemViewModel system = main.SystemView;

        Assert.NotNull(system.Plant);
        Assert.Contains("1 controller", system.Summary, StringComparison.Ordinal);
        Assert.False(system.HasFindings);
        Assert.NotEmpty(system.Layout.Boxes);

        await system.AddAsync(Fixture.PathOf("RobotCell.L5X"));

        Assert.Null(system.Error);
        Assert.Equal("Robot_PLC", Assert.Single(system.Others).Name);
        Assert.Contains("2 controllers", system.Summary, StringComparison.Ordinal);
        Assert.Equal(5, system.Findings.Count);
        Assert.Equal("5 problems between controllers", system.FindingsHeader);

        // The same file twice is refused.
        await system.AddAsync(Fixture.PathOf("RobotCell.L5X"));
        Assert.Single(system.Others);
        Assert.Contains("already open", system.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClickingABoxDescribesItAndOpeningAModuleGoesToTheHardwareTab()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        SystemViewModel system = main.SystemView;
        system.Add(Fixture.RobotCellAnalysed);

        PlantNode line3 = system.Plant!.Find("0:Local")!;
        system.SelectedNode = line3;
        Assert.Contains("Line3_PLC", system.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("→ Robot_PLC: MSG Robot_Read", system.SelectedDetails, StringComparison.Ordinal);
        Assert.Contains("← Robot_PLC: Heartbeat → Robot_Heartbeat", system.SelectedDetails, StringComparison.Ordinal);

        system.OpenNodeCommand.Execute(system.Plant.Find("0:VFD_101"));
        Assert.Equal(MainViewModel.HardwareTab, main.SelectedTab);
        Assert.Equal("VFD_101", main.SelectedModule!.Name);
    }

    [Fact]
    public async Task AnotherControllerCanBecomeTheMainProject()
    {
        var main = new MainViewModel();
        await main.OpenAsync(Fixture.PathOf("Line3.L5X"));
        await main.SystemView.AddAsync(Fixture.PathOf("RobotCell.L5X"));

        main.SystemView.MakeMainCommand.Execute(main.SystemView.Others[0]);
        for (int i = 0; i < 100 && main.Analysis?.Project.Controller.Name != "Robot_PLC"; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("Robot_PLC", main.Analysis!.Project.Controller.Name);
        Assert.Equal("Line3_PLC", Assert.Single(main.SystemView.Others).Name);
        Assert.Equal(5, main.SystemView.Findings.Count);
    }

    [Fact]
    public void ZoomStaysInRange()
    {
        var system = new MainViewModel().SystemView;
        system.Zoom = 5;
        Assert.Equal(2.0, system.Zoom);
        system.ZoomOutCommand.Execute(null);
        Assert.Equal("190%", system.ZoomText);
        system.Zoom = 0.01;
        Assert.Equal(0.3, system.Zoom);
    }
}
