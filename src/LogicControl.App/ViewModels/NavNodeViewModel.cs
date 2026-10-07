using LogicControl.App.Composition;

namespace LogicControl.App.ViewModels;

/// <summary>
/// One entry in the project navigator on the left: a folder (Hardware, Tasks, Programs...) or
/// a thing that opens - a module, a routine, an AOI.
/// </summary>
public sealed class NavNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public NavNodeViewModel(string title, string glyph, object? target = null, string? badge = null)
    {
        Title = title;
        Glyph = glyph;
        Target = target;
        Badge = badge ?? string.Empty;
    }

    public string Title { get; }

    /// <summary>A Segoe Fluent Icons code point - see <see cref="Glyphs"/>.</summary>
    public string Glyph { get; }

    /// <summary>What selecting it opens: a RoutineInfo, a HardwareNode, an AoiInfo... or null for a folder.</summary>
    public object? Target { get; }

    /// <summary>A count or short note shown muted at the right - "7 rungs", "ST", "192.168.1.20".</summary>
    public string Badge { get; }

    /// <summary>Error/Warning/Info/None - a finding on the thing itself.</summary>
    public string Level { get; init; } = ViewModels.Level.None;

    public List<NavNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// The navigator's icons, as Segoe Fluent Icons code points (with the MDL2 font as the fallback,
/// which has the same points for all of these). Text rather than image files: they take the
/// foreground colour, so they follow the theme with no work at all.
/// </summary>
public static class Glyphs
{
    public const string Controller = "\uE9F5";   // Processing
    public const string Folder = "\uE8B7";       // Folder
    public const string Module = "\uE950";       // Component
    public const string Network = "\uE968";      // Network
    public const string Drive = "\uE945";        // LightningBolt
    public const string Task = "\uE916";         // Stopwatch
    public const string Program = "\uE8F1";      // Library
    public const string Ladder = "\uE8FD";       // BulletedList
    public const string Text = "\uE943";         // Code
    public const string Block = "\uE8A9";        // ViewAll
    public const string Aoi = "\uEA86";          // Puzzle
    public const string DataType = "\uE8EC";     // Tag
    public const string Lock = "\uE72E";          // Lock
}
