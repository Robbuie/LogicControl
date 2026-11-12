// UseWPF drops System.IO from the implicit usings; this file reads and writes a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using LogicControl.Core.Analysis;

namespace LogicControl.App.Composition;

/// <summary>
/// Findings somebody has looked at and accepted - "that MSG really is fired from the HMI", "that
/// routine is kept for the next line" - kept per controller at
/// <c>%LOCALAPPDATA%\LogicControl\accepted.json</c>, so they stop showing every time the export is
/// opened again, and a new finding stands out.
///
/// <para>A finding is recognised by <see cref="Finding.Identity"/>: its rule, what it is about and
/// where, not its wording - a message that counts something still matches after the count
/// changes. Controllers are told apart by name, so a fresh export of the same controller keeps its
/// accepted findings wherever it is saved.</para>
///
/// <para>Like the recent list, reading or writing never throws, and a <c>null</c> path keeps
/// everything in memory - the tests, and the view model's default.</para>
/// </summary>
public sealed class AcceptedFindings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<string, Dictionary<string, DateTimeOffset>> _byController = new(StringComparer.OrdinalIgnoreCase);

    public AcceptedFindings(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Read();
    }

    public static string DefaultPath { get; } = Path.Combine(AppPaths.Data, "accepted.json");

    public bool IsAccepted(string controller, Finding finding) =>
        _byController.TryGetValue(controller, out Dictionary<string, DateTimeOffset>? accepted) && accepted.ContainsKey(finding.Identity);

    /// <summary>When <paramref name="finding"/> was accepted, or null.</summary>
    public DateTimeOffset? AcceptedAt(string controller, Finding finding) =>
        _byController.TryGetValue(controller, out Dictionary<string, DateTimeOffset>? accepted)
            && accepted.TryGetValue(finding.Identity, out DateTimeOffset at) ? at : null;

    public void Set(string controller, Finding finding, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(finding);
        if (accepted)
        {
            if (!_byController.TryGetValue(controller, out Dictionary<string, DateTimeOffset>? list))
            {
                list = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                _byController[controller] = list;
            }

            if (!list.TryAdd(finding.Identity, _clock()))
            {
                return;
            }
        }
        else if (!_byController.TryGetValue(controller, out Dictionary<string, DateTimeOffset>? list) || !list.Remove(finding.Identity))
        {
            return;
        }

        Write();
    }

    private void Read()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTimeOffset>>>(File.ReadAllText(_path), Options) is { } read)
            {
                _byController = new(read.ToDictionary(
                    p => p.Key,
                    p => new Dictionary<string, DateTimeOffset>(p.Value ?? [], StringComparer.Ordinal)), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            _byController = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_byController, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not worth a dialog: the finding shows again next time.
        }
    }
}
