using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LogicControl.App.Composition;
using LogicControl.Core.Analysis;
using LogicControl.Core.Assistant;
using LogicControl.Core.Authoring;
using LogicControl.Core.Authoring.History;
using LogicControl.Core.Logic;

namespace LogicControl.App.ViewModels.Assistant;

/// <summary>
/// The assistant panel: a chat with Claude that can read the open project and write drafts into
/// the Develop tab, the way the Claude extension works in VS Code.
///
/// <para>WPF-free like every view model here. The API key comes and goes through
/// <see cref="IApiKeyStore"/> (DPAPI in the app, memory in tests), and the HTTP handler can be
/// swapped, so a whole conversation - tools and all - runs in a test with no window and no
/// network.</para>
///
/// <para>Everything happens on the thread that called <see cref="SendAsync"/>: the stream's events
/// and the tool calls come back on it because every await keeps the context. In the app that is the
/// UI thread, which is what lets a draft tool change the Develop tab's set directly.</para>
///
/// <para><b>Two back ends</b> (<see cref="AssistantBackend"/>). With Claude Code - the default when
/// there is no API key - the panel runs the user's own <c>claude</c>, signed in with their Claude
/// plan, and its tool calls come back into this window over a named pipe
/// (<see cref="ToolBridgeServer"/>), posted to the UI thread so they touch the same drafts the
/// editors do. With an API key it calls the Messages API directly. The chat looks the same.</para>
/// </summary>
public sealed class AssistantViewModel : ObservableObject, IToolHost, IAssistantSink
{
    private readonly MainViewModel _main;
    private readonly IApiKeyStore _keys;
    private readonly Func<HttpMessageHandler?> _handler;
    private readonly LogicTools _tools;

    private readonly ClaudeCodeEnvironment _claudeCode;
    private IConversation? _session;
    private ClaudeClient? _client;
    private ToolBridgeServer? _bridge;
    private AssistantBackend _backend;
    private ClaudeCodeStatus _claudeStatus = new(ClaudeCodeState.Unknown, null, null);
    private bool _checking;
    private bool _checked;
    private CancellationTokenSource? _running;
    private ChatMessageViewModel? _current;
    private string _input = string.Empty;
    private string _model;
    private bool _isOpen;
    private bool _includeContext = true;
    private string? _status;
    private string _usageText = string.Empty;
    private bool _hasKey;
    private object? _lastDraft;

    /// <param name="backend">Null picks: an API key if one is stored, otherwise Claude Code.</param>
    public AssistantViewModel(
        MainViewModel main, IApiKeyStore keys, Func<HttpMessageHandler?>? handler = null, string? model = null,
        AssistantBackend? backend = null, ClaudeCodeEnvironment? claudeCode = null)
    {
        _main = main;
        _keys = keys;
        _handler = handler ?? (() => null);
        _model = model is { Length: > 0 } ? model : AssistantOptions.DefaultModel;
        _tools = new LogicTools(this);
        _hasKey = _keys.Load() is { Length: > 0 };
        _claudeCode = claudeCode ?? new ClaudeCodeEnvironment();
        _backend = backend ?? (_hasKey ? AssistantBackend.ApiKey : AssistantBackend.ClaudeCode);
        CheckClaudeCodeCommand = new RelayCommand(() => _ = CheckClaudeCodeAsync(), () => !_checking);

        SendCommand = new RelayCommand(() => _ = SendAsync(), () => CanSend);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        NewChatCommand = new RelayCommand(NewChat, () => !IsBusy && Items.Count > 0);
        AskCommand = new RelayParameterCommand(o =>
        {
            if (o is string prompt && prompt.Length > 0)
            {
                IsOpen = true;
                Input = prompt;
                _ = SendAsync();
            }
        }, o => o is string && !IsBusy);
        ForgetKeyCommand = new RelayCommand(ForgetKey, () => _hasKey);
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
    }

    // ------------------------------------------------------------------ state

    public ObservableCollection<object> Items { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value) && value && UsesClaudeCode && !_checked)
            {
                // First time the panel opens: find Claude Code, so the panel can say what is missing.
                _checked = true;
                _ = CheckClaudeCodeAsync();
            }
        }
    }

    public string Input
    {
        get => _input;
        set
        {
            if (SetProperty(ref _input, value ?? string.Empty))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy => _running is not null;

    public bool CanSend => !IsBusy && IsReady && _input.Trim().Length > 0;

    public bool HasKey => _hasKey;

    /// <summary>The API key card is shown: the API back end is chosen and there is no key.</summary>
    public bool NeedsKey => _backend == AssistantBackend.ApiKey && !_hasKey;

    /// <summary>
    /// The chat can be used: a key for the API, or a Claude Code that is not known to be missing or
    /// signed out (an unchecked one is let through - its first answer says what is wrong).
    /// </summary>
    public bool IsReady => _backend == AssistantBackend.ApiKey
        ? _hasKey
        : _claudeStatus.State is ClaudeCodeState.Ready or ClaudeCodeState.Unknown;

    // ------------------------------------------------------------------ back end

    public AssistantBackend Backend
    {
        get => _backend;
        set
        {
            if (SetProperty(ref _backend, value))
            {
                // A different back end is a different conversation.
                ResetSession();
                BackendChanged?.Invoke(this, EventArgs.Empty);
                RaiseReadiness();
                if (value == AssistantBackend.ClaudeCode)
                {
                    _ = CheckClaudeCodeAsync();
                }
            }
        }
    }

    public IReadOnlyList<AssistantBackend> Backends { get; } = [AssistantBackend.ClaudeCode, AssistantBackend.ApiKey];

    /// <summary>The back end as the header's drop-down index: 0 Claude Code, 1 API key.</summary>
    public int BackendIndex
    {
        get => _backend == AssistantBackend.ApiKey ? 1 : 0;
        set => Backend = value == 1 ? AssistantBackend.ApiKey : AssistantBackend.ClaudeCode;
    }

    /// <summary>Points the panel at a claude.exe the search did not find, and checks it.</summary>
    public void UseClaudeCodeAt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _claudeCode.ConfiguredPath = path;
        ResetSession();
        ClaudeCodePathChanged?.Invoke(this, EventArgs.Empty);
        _ = CheckClaudeCodeAsync();
    }

    /// <summary>Raised when the user picks a claude.exe, so the window can save it.</summary>
    public event EventHandler? ClaudeCodePathChanged;

    public string? ClaudeCodeConfiguredPath => _claudeCode.ConfiguredPath;

    /// <summary>Raised when the back end changes, so the window can save the choice.</summary>
    public event EventHandler? BackendChanged;

    public bool UsesClaudeCode => _backend == AssistantBackend.ClaudeCode;

    public ClaudeCodeStatus ClaudeCodeStatus => _claudeStatus;

    /// <summary>Claude Code is chosen and was not found.</summary>
    public bool ClaudeCodeMissing => UsesClaudeCode && _claudeStatus.State == ClaudeCodeState.NotInstalled;

    /// <summary>Claude Code is chosen, found, and not signed in.</summary>
    public bool ClaudeCodeSignedOut => UsesClaudeCode && _claudeStatus.State == ClaudeCodeState.SignedOut;

    /// <summary>One line under the header: which back end, and how it stands.</summary>
    public string BackendNote => _backend switch
    {
        AssistantBackend.ApiKey => _hasKey ? "API key - billed per use to the key's account." : "API key - none saved yet.",
        _ => _claudeStatus.State switch
        {
            ClaudeCodeState.Ready => $"Claude Code, signed in with {_claudeStatus.Detail ?? "your account"}.",
            ClaudeCodeState.NotInstalled => "Claude Code was not found on this PC.",
            ClaudeCodeState.SignedOut => "Claude Code is installed but not signed in.",
            _ => _checking ? "Looking for Claude Code..." : "Claude Code - uses your Claude plan.",
        },
    };

    /// <summary>The claude that will run, for the sign-in button. Null when not found.</summary>
    public string? ClaudeCodeExecutable => _claudeStatus.Executable;

    public RelayCommand CheckClaudeCodeCommand { get; }

    /// <summary>Looks for Claude Code and asks whether it is signed in. Never throws.</summary>
    public async Task CheckClaudeCodeAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        CheckClaudeCodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BackendNote));
        try
        {
            _claudeStatus = await _claudeCode.CheckAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _claudeStatus = new ClaudeCodeStatus(ClaudeCodeState.Unknown, null, ex.Message);
        }
        finally
        {
            _checking = false;
            CheckClaudeCodeCommand.NotifyCanExecuteChanged();
            RaiseReadiness();
        }
    }

    private void RaiseReadiness()
    {
        OnPropertyChanged(nameof(Backend));
        OnPropertyChanged(nameof(BackendIndex));
        OnPropertyChanged(nameof(UsesClaudeCode));
        OnPropertyChanged(nameof(NeedsKey));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ClaudeCodeStatus));
        OnPropertyChanged(nameof(ClaudeCodeMissing));
        OnPropertyChanged(nameof(ClaudeCodeSignedOut));
        OnPropertyChanged(nameof(ClaudeCodeExecutable));
        OnPropertyChanged(nameof(BackendNote));
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        AskCommand.NotifyCanExecuteChanged();
    }

    private void ResetSession()
    {
        (_session as IDisposable)?.Dispose();
        _session = null;
    }

    /// <summary>The conversation for the chosen back end, made on first use or after a change.</summary>
    private IConversation? EnsureSession()
    {
        if (_backend == AssistantBackend.ApiKey)
        {
            if (_keys.Load() is not { Length: > 0 } key)
            {
                return null;
            }

            _client ??= new ClaudeClient(key, _handler());
            if (_session is not ConversationSession api || api.Options.Model != _model)
            {
                IConversation? previous = _session;
                ResetSession();
                _session = new ConversationSession(_client, _tools, new AssistantOptions { Model = _model });
                if (previous is { MessageCount: > 0 })
                {
                    // The history lives in the session; a model switch mid-chat starts a fresh one, and says so.
                    Add(new ChatNoticeViewModel($"Switched to {_model} - it starts without the earlier messages."));
                }
            }

            return _session;
        }

        if (_session is ClaudeCodeSession code)
        {
            // Claude Code keeps the history itself; a new model resumes the same session.
            code.SetModel(_model);
            return code;
        }

        ResetSession();
        string exe = _claudeStatus.Executable ?? _claudeCode.Locator.Find(_claudeCode.ConfiguredPath)
            ?? throw new ClaudeApiException("Claude Code was not found on this PC. Install it, or switch the panel to an API key.");

        _bridge ??= new ToolBridgeServer(ToolBridgeServer.NewPipeName(), RunToolOnThisThread());
        Directory.CreateDirectory(_claudeCode.DataFolder);
        string mcp = Path.Combine(_claudeCode.DataFolder, "mcp.json");
        string prompt = Path.Combine(_claudeCode.DataFolder, "system-prompt.txt");
        File.WriteAllText(mcp, McpConfig(_claudeCode.SelfExecutable ?? "LogicControl.exe", _bridge.PipeName));
        File.WriteAllText(prompt, AssistantPrompt.System);

        _session = new ClaudeCodeSession(
            new ClaudeCodeOptions
            {
                Executable = exe,
                McpConfigFile = mcp,
                SystemPromptFile = prompt,
                WorkingDirectory = Path.Combine(_claudeCode.DataFolder, "work"),
                Model = _model,
            },
            _claudeCode.Launcher);
        return _session;
    }

    /// <summary>The MCP configuration Claude Code is started with: LogicControl, attached to this window.</summary>
    internal static string McpConfig(string selfExecutable, string pipe) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                [ClaudeCodeSession.ServerName] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = selfExecutable,
                    ["args"] = new System.Text.Json.Nodes.JsonArray("--mcp", "--attach", pipe),
                },
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// How a tool call from the pipe runs: on the thread this was made on - the UI thread in the
    /// app - because the tools read the open project and write the Develop tab's drafts.
    /// </summary>
    private Func<string, JsonElement, Task<ToolResult>> RunToolOnThisThread()
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        return (name, input) =>
        {
            if (context is null)
            {
                return Task.FromResult(_tools.Execute(name, input));
            }

            var done = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ =>
            {
                try
                {
                    done.SetResult(_tools.Execute(name, input));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
                {
                    done.SetResult(ToolResult.Fail(ex.Message));
                }
            }, null);
            return done.Task;
        };
    }

    /// <summary>Stops Claude Code and the pipe - the window is closing.</summary>
    public void Shutdown()
    {
        ResetSession();
        _bridge?.Dispose();
        _bridge = null;
    }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Send what the user is looking at - the open routine, the selected tag or draft - with each question.</summary>
    public bool IncludeContext
    {
        get => _includeContext;
        set => SetProperty(ref _includeContext, value);
    }

    public IReadOnlyList<string> Models => AssistantOptions.Models;

    /// <summary>How rungs in answers are drawn: built-in instructions plus the project's and the drafts' AOIs.</summary>
    public Func<string, InstructionShape?> Shapes => _main.Develop.Shapes;

    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, string.IsNullOrWhiteSpace(value) ? AssistantOptions.DefaultModel : value.Trim()))
            {
                // A new model needs a new session object; the history carries over.
                ModelChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when the model changes, so the window can save the choice.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>What is happening right now: "Reading MainProgram/MainRoutine...", or null.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    /// <summary>Starting points shown in an empty chat.</summary>
    public IReadOnlyList<string> Suggestions =>
        _main.Analysis is null
            ? [
                "Draft a UDT and rungs for a two-speed conveyor motor with a jam timer",
                "Write an Add-On Instruction for a duty/standby pump pair",
            ]
            : [
                "Give me an overview of this controller and how it talks to other devices",
                "Explain the routine I have open",
                "Go through the findings and tell me which ones matter",
                "Which outputs have more than one rung writing them?",
            ];

    public RelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand NewChatCommand { get; }

    /// <summary>Sends its parameter as a question - the suggestion buttons and "Ask about this".</summary>
    public RelayParameterCommand AskCommand { get; }

    public RelayCommand ForgetKeyCommand { get; }

    public RelayCommand ToggleCommand { get; }

    // ------------------------------------------------------------------ key

    /// <summary>Stores a key (encrypted for this Windows user) and makes the chat usable.</summary>
    public string? SaveKey(string? key)
    {
        string k = key?.Trim() ?? string.Empty;
        if (!k.StartsWith("sk-ant-", StringComparison.Ordinal) || k.Length < 20)
        {
            return "That does not look like an Anthropic API key - they start with sk-ant-.";
        }

        _keys.Save(k);
        _client?.Dispose();
        _client = null;
        if (_session is ConversationSession)
        {
            ResetSession();
        }

        SetHasKey(true);
        return null;
    }

    private void ForgetKey()
    {
        _keys.Clear();
        _client?.Dispose();
        _client = null;
        if (_session is ConversationSession)
        {
            ResetSession();
        }

        SetHasKey(false);
    }

    private void SetHasKey(bool value)
    {
        _hasKey = value;
        OnPropertyChanged(nameof(HasKey));
        ForgetKeyCommand.NotifyCanExecuteChanged();
        RaiseReadiness();
    }

    // ------------------------------------------------------------------ chat

    /// <summary>Sends the input box. Never throws: API failures land in the chat as an error line.</summary>
    public async Task SendAsync()
    {
        string text = _input.Trim();
        IsOpen = true;
        if (text.Length == 0 || IsBusy || !IsReady)
        {
            // Not ready yet: the question stays in the box and the panel shows what is missing.
            return;
        }

        IConversation? session;
        try
        {
            session = EnsureSession();
        }
        catch (Exception ex) when (ex is ClaudeApiException or IOException or UnauthorizedAccessException)
        {
            Add(new ChatNoticeViewModel(ex.Message, isError: true));
            return;
        }

        if (session is null)
        {
            return;
        }

        Input = string.Empty;

        // Anything typed in the Develop tab gets its own revision before Claude changes the
        // drafts, so the history keeps the person's edit and Claude's apart.
        _main.Develop.CommitPending();

        Add(new ChatMessageViewModel(ChatRole.User, text));
        _current = null;
        _lastDraft = null;
        _running = new CancellationTokenSource();
        RaiseBusy();
        Status = UsesClaudeCode && session.MessageCount == 0 ? "Starting Claude Code..." : "Thinking...";

        try
        {
            await session.RunTurnAsync(text, _includeContext ? BuildContext() : null, this, _running.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Add(new ChatNoticeViewModel("Stopped."));
        }
        catch (ClaudeApiException ex)
        {
            Add(new ChatNoticeViewModel(ex.Message, isError: true));
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _current = null;
            Status = null;
            RaiseBusy();
        }
    }

    private void Stop() => _running?.Cancel();

    private void NewChat()
    {
        _session?.Reset();
        Items.Clear();
        UsageText = string.Empty;
        OnPropertyChanged(nameof(IsEmpty));
        NewChatCommand.NotifyCanExecuteChanged();
    }

    private void Add(object item)
    {
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        NewChatCommand.NotifyCanExecuteChanged();
    }

    private void RaiseBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the user is looking at, for the model to resolve "this routine" and "that tag". Short on
    /// purpose: anything bigger is a tool call away, and this goes with every message.
    /// </summary>
    internal string BuildContext()
    {
        var sb = new StringBuilder();
        if (_main.Analysis is { } a)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Open project: {a.Project.Controller.Name} ({Path.GetFileName(a.Project.SourcePath)})");
        }
        else
        {
            sb.AppendLine("No project is open; drafting still works.");
        }

        string tab = _main.SelectedTab switch
        {
            MainViewModel.OverviewTab => "Overview",
            MainViewModel.HardwareTab => "Hardware",
            MainViewModel.CommsTab => "Communications",
            MainViewModel.SystemTab => "System",
            MainViewModel.TagsTab => "Tags",
            MainViewModel.LogicTab => "Logic",
            MainViewModel.FindingsTab => "Findings",
            _ => "Develop",
        };
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tab: {tab}");

        if (_main.Routine?.Routine is { } r && _main.SelectedTab == MainViewModel.LogicTab)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Routine on screen: {r.Owner}/{r.Name} ({r.Rungs.Count} rungs)");
        }

        if (_main.SelectedTag is { } t && _main.SelectedTab == MainViewModel.TagsTab)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Selected tag: {t.Tag.QualifiedName} ({t.DataType})");
        }

        if (_main.SelectedTab == MainViewModel.DevelopTab && _main.Develop.SelectedItem is { } d)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Draft selected in the Develop tab: {d.Kind} {d.Name}");
        }

        if (!_main.Develop.IsEmpty)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Develop tab: {_main.Develop.Items.Count - 1} draft(s), {_main.Develop.ErrorCount} error(s) - list_drafts shows them.");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ IAssistantSink

    void IAssistantSink.TextDelta(string text)
    {
        if (_current is null)
        {
            _current = new ChatMessageViewModel(ChatRole.Assistant, string.Empty);
            Add(_current);
        }

        _current.Append(text);
        Status = null;
    }

    void IAssistantSink.ToolStarted(string id, string name, JsonElement input)
    {
        _current = null;
        var activity = new ToolActivityViewModel(id, name, Describe(name, input));
        Add(activity);
        Status = activity.Summary + "...";
    }

    void IAssistantSink.ToolFinished(string id, string name, ToolResult result)
    {
        if (Items.OfType<ToolActivityViewModel>().LastOrDefault(t => t.Id == id) is { } activity)
        {
            activity.Finish(result);
        }

        Status = "Thinking...";
    }

    void IAssistantSink.UsageUpdated(Usage total) => UsageText = total.ToString();

    void IAssistantSink.Notice(string text) => Add(new ChatNoticeViewModel(text));

    /// <summary>A tool call in words, for the activity line: "Reading MainProgram/MainRoutine".</summary>
    internal static string Describe(string name, JsonElement input)
    {
        string Arg(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        int Count(string key) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

        return name switch
        {
            "project_overview" => "Reading the project overview",
            "list_routines" => Arg("program") is { Length: > 0 } p ? $"Listing routines in {p}" : "Listing routines",
            "read_routine" => $"Reading {Arg("program")}/{Arg("routine")}",
            "find_tags" => Arg("query") is { Length: > 0 } q ? $"Searching tags for '{q}'" : "Listing tags",
            "tag_references" => $"Cross-referencing {Arg("tag")}",
            "get_data_type" => $"Looking up {Arg("name")}",
            "list_findings" => "Reading the findings",
            "list_communications" => "Reading the communications map",
            "list_hardware" => "Reading the I/O tree",
            "instruction_help" => $"Checking {Arg("mnemonic").ToUpperInvariant()}",
            "check_rungs" => $"Checking {Count("rungs")} rung(s)",
            "draft_data_type" => $"Drafting data type {Arg("name")}",
            "draft_tags" => $"Drafting {Count("tags")} tag(s)",
            "draft_routine" => $"Drafting {Arg("program")}/{Arg("routine")} ({Count("rungs")} rung(s))",
            "draft_aoi" => $"Drafting Add-On {Arg("name")}",
            "list_drafts" => "Reading the Develop tab",
            "remove_draft" => $"Removing draft {Arg("name")}",
            _ => name,
        };
    }

    // ------------------------------------------------------------------ IToolHost

    ProjectAnalysis? IToolHost.Analysis => _main.Analysis;

    DevelopmentSet IToolHost.Drafts => _main.Develop.Set;

    bool IToolHost.CanOpenProjects => false;

    string? IToolHost.OpenProject(string path) => "Projects are opened from LogicControl's File menu.";

    void IToolHost.DraftsChanged(string summary)
    {
        // Point the Develop tab at the newest draft so the user sees it appear.
        DevelopmentSet set = _main.Develop.Set;
        object? newest = set.Routines.LastOrDefault() as object ?? set.AddOnInstructions.LastOrDefault() as object ?? set.DataTypes.LastOrDefault();
        _lastDraft = newest;
        _main.Develop.ChangedElsewhere(null, "Changed by the assistant - review before exporting.", summary, RevisionAuthor.Claude);
    }

    /// <summary>Opens the Develop tab on what the assistant drafted last - the "Open in Develop" link.</summary>
    public void ShowDrafts()
    {
        _main.StartDeveloping();
        if (_lastDraft is not null)
        {
            _main.Develop.Select(_lastDraft);
        }
    }
}

public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>A message in the chat. An assistant message grows as it streams.</summary>
public sealed class ChatMessageViewModel(ChatRole role, string text) : ObservableObject
{
    private readonly StringBuilder _text = new(text);
    private IReadOnlyList<ChatSegment>? _segments;

    public ChatRole Role { get; } = role;

    public bool IsUser => Role == ChatRole.User;

    public string Text => _text.ToString();

    /// <summary>The text cut into prose, code and rungs - rungs are drawn as ladder under their text.</summary>
    public IReadOnlyList<ChatSegment> Segments => _segments ??= ChatSegment.Parse(Text);

    internal void Append(string piece)
    {
        _text.Append(piece);
        _segments = null;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Segments));
    }
}

/// <summary>A line in the chat that is not Claude talking: stopped, an API error, a model switch.</summary>
public sealed class ChatNoticeViewModel(string text, bool isError = false)
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;

    public string Level => IsError ? ViewModels.Level.Error : ViewModels.Level.Info;
}

/// <summary>One tool call: what it is doing, whether it worked, and - on request - what it returned.</summary>
public sealed class ToolActivityViewModel(string id, string name, string summary) : ObservableObject
{
    private string _level = ViewModels.Level.None;
    private string? _detail;
    private bool _expanded;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Summary { get; } = summary;

    public bool IsDraft => Name.StartsWith("draft_", StringComparison.Ordinal) || Name == "remove_draft";

    public bool IsRunning => _detail is null;

    /// <summary>None while running, Info when done, Warning for a draft with check errors, Error when the tool failed.</summary>
    public string Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    internal void Finish(ToolResult result)
    {
        Detail = result.Text.Length > 4000 ? result.Text[..4000] + "\n..." : result.Text;
        Level = result.IsError ? ViewModels.Level.Error
            : IsDraft && result.Text.Contains("ERROR", StringComparison.Ordinal) ? ViewModels.Level.Warning
            : ViewModels.Level.Info;
        OnPropertyChanged(nameof(IsRunning));
    }
}

/// <summary>
/// A run of a chat message: prose, a code block, or a rung. Markdown is kept to what an answer here
/// uses - paragraphs, lists, **bold** and fenced code - and a code line that parses as a rung
/// becomes a <see cref="ChatSegmentKind.Rung"/>, which the panel draws as ladder.
/// </summary>
public sealed record ChatSegment(ChatSegmentKind Kind, string Text)
{
    public bool IsText => Kind == ChatSegmentKind.Text;

    public bool IsCode => Kind == ChatSegmentKind.Code;

    public bool IsRung => Kind == ChatSegmentKind.Rung;

    public static IReadOnlyList<ChatSegment> Parse(string text)
    {
        var segments = new List<ChatSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        string[] parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("```");
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (i % 2 == 0)
            {
                string prose = Prose(part);
                if (prose.Length > 0)
                {
                    segments.Add(new ChatSegment(ChatSegmentKind.Text, prose));
                }

                continue;
            }

            // A fence: drop the language tag on its first line, then split rungs from other code.
            int newline = part.IndexOf('\n', StringComparison.Ordinal);
            string body = newline >= 0 && !part[..newline].Contains('(', StringComparison.Ordinal) ? part[(newline + 1)..] : part;
            var code = new StringBuilder();

            foreach (string raw in body.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (LooksLikeRung(line))
                {
                    Flush(segments, code);
                    segments.Add(new ChatSegment(ChatSegmentKind.Rung, line.Trim()));
                }
                else if (line.Length > 0 || code.Length > 0)
                {
                    code.AppendLine(line);
                }
            }

            Flush(segments, code);
        }

        return segments;
    }

    private static bool LooksLikeRung(string line)
    {
        string t = line.Trim();
        if (!t.EndsWith(';') || t.Length < 4)
        {
            return false;
        }

        LadderRung rung = LadderParser.Parse(t);
        return rung.IsValid && rung.Instructions.Any();
    }

    private static void Flush(List<ChatSegment> segments, StringBuilder code)
    {
        string c = code.ToString().TrimEnd();
        if (c.Length > 0)
        {
            segments.Add(new ChatSegment(ChatSegmentKind.Code, c));
        }

        code.Clear();
    }

    /// <summary>Plain text from light markdown: headings lose their #, bold loses its asterisks, inline code its backticks.</summary>
    private static string Prose(string s)
    {
        var lines = s.Split('\n').Select(l =>
        {
            string t = l.TrimEnd();
            int hashes = 0;
            while (hashes < t.Length && t[hashes] == '#')
            {
                hashes++;
            }

            if (hashes is > 0 and < 7 && hashes < t.Length && t[hashes] == ' ')
            {
                t = t[(hashes + 1)..];
            }

            if (t.StartsWith("* ", StringComparison.Ordinal))
            {
                t = "- " + t[2..];
            }

            return t.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        });

        return string.Join('\n', lines).Trim('\n');
    }
}

public enum ChatSegmentKind
{
    Text,
    Code,
    Rung,
}

/// <summary>Where the API key lives. The app encrypts it for the Windows user; tests keep it in memory.</summary>
public interface IApiKeyStore
{
    string? Load();

    void Save(string key);

    void Clear();
}

/// <summary>A key kept in memory only - the default when nothing better is given, and the tests' store.</summary>
public sealed class MemoryKeyStore(string? key = null) : IApiKeyStore
{
    private string? _key = key;

    public string? Load() => _key;

    public void Save(string key) => _key = key;

    public void Clear() => _key = null;
}
