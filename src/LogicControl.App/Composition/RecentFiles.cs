// UseWPF drops System.IO from the implicit usings; this file reads and writes a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogicControl.App.Composition;

/// <summary>What a recent file is, which decides how File &gt; Open recent opens it.</summary>
public enum RecentKind
{
    /// <summary>A Studio 5000 L5X export - opened, compared with, or added to the system view.</summary>
    Export,

    /// <summary>A LogicControl development set (.lcdev) - opened on the Develop tab.</summary>
    DevelopmentSet,
}

/// <summary>One line of the recent list.</summary>
public sealed record RecentFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] RecentKind Kind,
    [property: JsonPropertyName("opened")] DateTimeOffset Opened)
{
    /// <summary>The file name, for the menu.</summary>
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The folder it is in, for the menu's second column.</summary>
    [JsonIgnore]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>
/// The files opened lately, newest first, at <c>%LOCALAPPDATA%\LogicControl\recent.json</c>.
///
/// <para>Its own file, for the reason <c>appearance.json</c> is: <c>settings.json</c> is site
/// configuration the app never writes. This one the app owns and rewrites on every open.</para>
///
/// <para>Paths compare case-insensitively (Windows). A file that has since gone - a USB stick
/// pulled, a share offline - stays in the list, shown as missing, until somebody clears it: a share
/// that is offline now is usually back tomorrow.</para>
///
/// <para>Reading or writing it never throws. A list that could not be saved is a list that is
/// shorter next time, which is not worth a dialog. A <c>null</c> path keeps the list in memory
/// only - the tests, and the view model's default.</para>
/// </summary>
public sealed class RecentFiles
{
    /// <summary>How many of each kind are kept.</summary>
    public const int MaxPerKind = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _path;
    private readonly Func<string, bool> _exists;
    private readonly Func<DateTimeOffset> _clock;
    private List<RecentFile> _items = [];

    /// <param name="path">The file the list is kept in; null keeps it in memory.</param>
    /// <param name="exists">Whether a path is there now - File.Exists unless a test says otherwise.</param>
    /// <param name="clock">The time an open is stamped with.</param>
    public RecentFiles(string? path = null, Func<string, bool>? exists = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _exists = exists ?? File.Exists;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Read();
    }

    /// <summary>The default file.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(AppPaths.Data, "recent.json");

    /// <summary>Raised after every change, so the menu can rebuild.</summary>
    public event EventHandler? Changed;

    /// <summary>Everything, newest first.</summary>
    public IReadOnlyList<RecentFile> All => _items;

    /// <summary>Recent L5X exports, newest first.</summary>
    public IReadOnlyList<RecentFile> Exports => Of(RecentKind.Export);

    /// <summary>Recent development sets, newest first.</summary>
    public IReadOnlyList<RecentFile> DevelopmentSets => Of(RecentKind.DevelopmentSet);

    public bool IsEmpty => _items.Count == 0;

    /// <summary>Whether the file is still where it was.</summary>
    public bool Exists(RecentFile file) => _exists(file.Path);

    /// <summary>Whether any line points at a file that is no longer there.</summary>
    public bool HasMissing => _items.Any(f => !_exists(f.Path));

    /// <summary>Moves <paramref name="path"/> to the top of its kind's list.</summary>
    public void Add(string path, RecentKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full = Normalise(path);
        _items.RemoveAll(f => Same(f.Path, full));
        _items.Insert(0, new RecentFile(full, kind, _clock()));
        Trim();
        Write();
    }

    /// <summary>Takes one line off the list.</summary>
    public void Remove(string path)
    {
        if (_items.RemoveAll(f => Same(f.Path, path)) > 0)
        {
            Write();
        }
    }

    /// <summary>Takes off every line whose file is no longer there.</summary>
    public void RemoveMissing()
    {
        if (_items.RemoveAll(f => !_exists(f.Path)) > 0)
        {
            Write();
        }
    }

    public void Clear()
    {
        if (_items.Count > 0)
        {
            _items.Clear();
            Write();
        }
    }

    private List<RecentFile> Of(RecentKind kind) => [.. _items.Where(f => f.Kind == kind)];

    private void Trim()
    {
        var kept = new List<RecentFile>();
        var counts = new Dictionary<RecentKind, int>();
        foreach (RecentFile f in _items)
        {
            counts.TryGetValue(f.Kind, out int n);
            if (n < MaxPerKind)
            {
                kept.Add(f);
                counts[f.Kind] = n + 1;
            }
        }

        _items = kept;
    }

    private void Read()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            List<RecentFile>? read = JsonSerializer.Deserialize<List<RecentFile>>(File.ReadAllText(_path), Options);
            _items = read is null
                ? []
                : [.. read
                    .Where(f => !string.IsNullOrWhiteSpace(f.Path) && Enum.IsDefined(f.Kind))
                    .OrderByDescending(f => f.Opened)
                    .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            _items = [];
        }
    }

    private void Write()
    {
        if (_path is not null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_items, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A recent list that did not save is a shorter list next time - not worth a dialog.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Normalise(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
