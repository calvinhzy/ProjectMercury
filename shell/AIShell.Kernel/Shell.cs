using System.Reflection;
using Microsoft.PowerShell;
using AIShell.Abstraction;
using AIShell.Kernel.Commands;
using Spectre.Console;

namespace AIShell.Kernel;

internal sealed class Shell : IShell
{
    private readonly bool _isInteractive;
    private readonly string _version;
    private readonly List<LLMAgent> _agents;
    private readonly Stack<LLMAgent> _activeAgentStack;
    private readonly ShellWrapper _wrapper;
    private readonly HashSet<string> _textToIgnore;
    private readonly Setting _setting;
    private CancellationTokenSource _cancellationSource;

    /// <summary>
    /// Indicates if we want to exit the shell.
    /// </summary>
    internal bool Exit { set; get; }

    /// <summary>
    /// Indicates if we want to regenerate for the last query.
    /// </summary>
    internal bool Regenerate { set; get; }

    /// <summary>
    /// The last query sent by user.
    /// </summary>
    internal string LastQuery { private set; get; }

    /// <summary>
    /// The agent that served the last query.
    /// </summary>
    internal LLMAgent LastAgent { private set; get; }

    /// <summary>
    /// Gets the host.
    /// </summary>
    internal Host Host { get; }

    /// <summary>
    /// Gets the command runner.
    /// </summary>
    internal CommandRunner CommandRunner { get; }

    /// <summary>
    /// Gets the channel to the command-line shell.
    /// </summary>
    internal Channel Channel { get; }

    /// <summary>
    /// Gets the event handler that will be set when initialization is done.
    /// </summary>
    internal ManualResetEvent InitEventHandler { get; }

    /// <summary>
    /// Gets the agent list.
    /// </summary>
    internal List<LLMAgent> Agents => _agents;

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    internal CancellationToken CancellationToken => _cancellationSource.Token;

    /// <summary>
    /// Gets the currently active agent.
    /// </summary>
    internal LLMAgent ActiveAgent => _activeAgentStack.TryPeek(out var agent) ? agent : null;

    #region IShell implementation

    IHost IShell.Host => Host;
    CancellationToken IShell.CancellationToken => _cancellationSource.Token;

    #endregion IShell implementation

    /// <summary>
    /// Creates an instance of <see cref="Shell"/>.
    /// </summary>
    internal Shell(bool interactive, ShellArgs args)
    {
        _isInteractive = interactive;
        _wrapper = args.ShellWrapper;

        // Create the channel if the args is specified.
        // The channel object starts the connection initialization on a background thread,
        // to run in parallel with the rest of the Shell initialization.
        InitEventHandler = new ManualResetEvent(false);
        Channel = args.Channel is null ? null : new Channel(args.Channel, this);

        _agents = [];
        _setting = Setting.Load();
        _activeAgentStack = new Stack<LLMAgent>();
        _textToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _cancellationSource = new CancellationTokenSource();
        _version = typeof(Shell).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        Exit = false;
        Regenerate = false;
        LastQuery = null;
        LastAgent = null;
        Host = new Host();

        if (interactive)
        {
            ShowBanner();
            CommandRunner = new CommandRunner(this);
            SetReadLineExperience();
        }

        LoadAvailableAgents();
        Console.CancelKeyPress += OnCancelKeyPress;
        InitEventHandler.Set();

        if (interactive)
        {
            ShowLandingPage();
        }
    }

    internal void ShowBanner()
    {
        string banner = _wrapper?.Banner is null ? "AI Shell" : _wrapper.Banner;
        string version = _wrapper?.Version is null ? _version : _wrapper.Version;
        Host.MarkupLine($"[bold]{banner.EscapeMarkup()}[/]")
            .MarkupLine($"[grey]{version.EscapeMarkup()}[/]")
            .WriteLine();
    }

    internal void ShowLandingPage()
    {
        // Write out information about the active agent.
        var current = ActiveAgent;
        if (current is not null)
        {
            bool isWrapped = true;
            if (!current.Impl.Name.Equals(_wrapper?.Agent, StringComparison.OrdinalIgnoreCase))
            {
                isWrapped = false;
                Host.MarkupLine($"Using the agent [green]{current.Impl.Name}[/]:");
            }

            current.Display(Host, isWrapped ? _wrapper.Description : null);
        }

        // Write out help.
        Host.MarkupLine($"Run {Formatter.Command("/help")} for more instructions.")
            .WriteLine();
    }

    /// <summary>
    /// Get all code blocks from the last LLM response.
    /// </summary>
    /// <returns></returns>
    internal List<CodeBlock> GetCodeBlockFromLastResponse()
    {
        return Host.MarkdownRender.GetAllCodeBlocks();
    }

    internal void OnUserAction(UserActionPayload actionPayload)
    {
        if (actionPayload.Action is UserAction.CodeCopy)
        {
            var codePayload = (CodePayload)actionPayload;
            _textToIgnore.Add(codePayload.Code);
        }

        if (LastAgent.Impl.CanAcceptFeedback(actionPayload.Action))
        {
            var state = Tuple.Create(LastAgent.Impl, actionPayload);
            ThreadPool.QueueUserWorkItem(
                callBack: static tuple => tuple.Item1.OnUserAction(tuple.Item2),
                state: state,
                preferLocal: false);
        }
    }

    /// <summary>
    /// Load a plugin assembly file and process the agents defined in it.
    /// </summary>
    internal void ProcessAgentPlugin(string pluginName, string pluginDir, string pluginFile)
    {
        AgentAssemblyLoadContext context = new(pluginName, pluginDir);
        Assembly plugin = context.LoadFromAssemblyPath(pluginFile);

        foreach (Type type in plugin.ExportedTypes)
        {
            if (!typeof(ILLMAgent).IsAssignableFrom(type))
            {
                continue;
            }

            var agent = (ILLMAgent)Activator.CreateInstance(type);
            var agentHome = Path.Join(Utils.AgentConfigHome, agent.Name);
            var config = new AgentConfig
            {
                IsInteractive = _isInteractive,
                ConfigurationRoot = Directory.CreateDirectory(agentHome).FullName,
                RenderingStyle = Console.IsOutputRedirected
                    ? RenderingStyle.FullResponsePreferred
                    : RenderingStyle.StreamingResponsePreferred,
                Context = agent.Name.Equals(_wrapper?.Agent, StringComparison.OrdinalIgnoreCase)
                    ? _wrapper.Context
                    : null
            };

            agent.Initialize(config);
            _agents.Add(new LLMAgent(agent, context));
        }
    }

    /// <summary>
    /// Load all available agents.
    /// </summary>
    private void LoadAvailableAgents()
    {
        string inboxAgentHome = Path.Join(AppContext.BaseDirectory, "agents");
        IEnumerable<string> agentDirs = Enumerable.Concat(
            Directory.EnumerateDirectories(inboxAgentHome),
            Directory.EnumerateDirectories(Utils.AgentHome));

        foreach (string dir in agentDirs)
        {
            string name = Path.GetFileName(dir);
            string file = Path.Join(dir, $"{name}.dll");

            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                ProcessAgentPlugin(name, dir, file);
            }
            catch (Exception ex)
            {
                Host.WriteErrorLine($"Failed to load the agent '{name}': {ex.Message}\n");
            }
        }

        if (_agents.Count is 0)
        {
            Host.MarkupWarningLine($"No agent available.\n");
            return;
        }

        try
        {
            LLMAgent chosenAgent = null;
            string active = _wrapper?.Agent ?? _setting.DefaultAgent;
            if (!string.IsNullOrEmpty(active))
            {
                foreach (LLMAgent agent in _agents)
                {
                    if (agent.Impl.Name.Equals(active, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenAgent = agent;
                        break;
                    }
                }

                if (chosenAgent is null)
                {
                    Host.MarkupWarningLine($"The configured active agent '{active}' is not available.\n");
                }
                else if (_wrapper?.Prompt is not null)
                {
                    chosenAgent.Prompt = _wrapper.Prompt;
                }
            }

            // If there is only 1 agent available, use it as the active one.
            // Otherwise, ask user to choose the active one from the list.
            chosenAgent ??= _agents.Count is 1
                ? _agents[0]
                : Host.PromptForSelectionAsync(
                    title: "[orange1]Please select an [Blue]agent[/] to use[/]:",
                    choices: _agents,
                    converter: static a => a.Impl.Name)
                .GetAwaiter().GetResult();

            PushActiveAgent(chosenAgent);
        }
        catch (Exception)
        {
            // Ignore failure from showing the confirmation prompt.
        }
    }

    /// <summary>
    /// Push the active agent on to the stack.
    /// </summary>
    internal void PushActiveAgent(LLMAgent agent)
    {
        if (_activeAgentStack.Count is 2)
        {
            throw new InvalidOperationException("Cannot push when two agents are already on stack.");
        }

        bool loadCommands = false;
        if (_isInteractive)
        {
            loadCommands = !_activeAgentStack.TryPeek(out var current) || current != agent;
        }

        _activeAgentStack.Push(agent);
        if (loadCommands)
        {
            ILLMAgent impl = agent.Impl;
            impl.RefreshChat();
            CommandRunner.UnloadAgentCommands();
            CommandRunner.LoadCommands(impl.GetCommands(), impl.Name);
        }
    }

    /// <summary>
    /// Pop the current active agent off the stack.
    /// </summary>
    internal void PopActiveAgent()
    {
        if (_activeAgentStack.Count != 2)
        {
            throw new InvalidOperationException("Cannot pop when only one active agent is on stack.");
        }

        LLMAgent pre = _activeAgentStack.Pop();
        LLMAgent cur = _activeAgentStack.Peek();

        if (pre != cur)
        {
            ILLMAgent impl = cur.Impl;
            CommandRunner.UnloadAgentCommands();
            CommandRunner.LoadCommands(impl.GetCommands(), impl.Name);
        }
    }

    /// <summary>
    /// Switch and use another agent as the active.
    /// </summary>
    internal void SwitchActiveAgent(LLMAgent agent)
    {
        ILLMAgent impl = agent.Impl;
        if (_activeAgentStack.TryPop(out var current))
        {
            _activeAgentStack.Clear();
            _activeAgentStack.Push(agent);
            if (current != agent)
            {
                impl.RefreshChat();
                CommandRunner.UnloadAgentCommands();
                CommandRunner.LoadCommands(impl.GetCommands(), impl.Name);
            }
        }
        else
        {
            _activeAgentStack.Push(agent);
            impl.RefreshChat();
            CommandRunner.LoadCommands(impl.GetCommands(), impl.Name);
        }
    }

    /// <summary>
    /// For reference:
    /// https://github.com/dotnet/command-line-api/blob/67df30a1ac4152e7f6278847b88b8f1ea1492ba7/src/System.CommandLine/Invocation/ProcessTerminationHandler.cs#L73
    /// TODO: We may want to implement `OnPosixSignal` too for more reliable cancellation on non-Windows.
    /// </summary>
    private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs args)
    {
        // Set the Cancel property to true to prevent the process from terminating.
        args.Cancel = true;
        switch (args.SpecialKey)
        {
            // Treat both Ctrl-C and Ctrl-Break as the same.
            case ConsoleSpecialKey.ControlC:
            case ConsoleSpecialKey.ControlBreak:
                // Request cancellation and refresh the cancellation source.
                _cancellationSource.Cancel();
                _cancellationSource = new CancellationTokenSource();
                return;
        }
    }

    /// <summary>
    /// Configure the read-line experience.
    /// </summary>
    private void SetReadLineExperience()
    {
        Utils.SetDefaultKeyHandlers();
        ReadLineHelper helper = new(this, CommandRunner);
        PSConsoleReadLine.GetOptions().ReadLineHelper = helper;
    }

    /// <summary>
    /// Execute a command.
    /// </summary>
    /// <param name="input">Command line to be executed.</param>
    private void RunCommand(string input)
    {
        string commandLine = input[1..].Trim();
        if (commandLine == string.Empty)
        {
            Host.WriteErrorLine("Command is missing.");
            return;
        }

        try
        {
            CommandRunner.InvokeCommand(commandLine);
        }
        catch (Exception e)
        {
            Host.WriteErrorLine(e.Message);
        }
    }

    private string TaggingAgent(string input, ref LLMAgent agent)
    {
        input = input.TrimEnd();
        int index = input.IndexOf(' ');

        string targetName = index is -1 ? input[1..] : input[1..index];
        if (string.IsNullOrEmpty(targetName))
        {
            Host.WriteErrorLine("No target agent specified after '@'.");
            return null;
        }

        if (_agents.Count is 0)
        {
            Host.WriteErrorLine("No agent is available.");
            return null;
        }

        LLMAgent targetAgent = AgentCommand.FindAgent(targetName, this);
        if (targetAgent is null)
        {
            AgentCommand.AgentNotFound(targetName, this);
            return null;
        }

        agent = targetAgent;
        SwitchActiveAgent(targetAgent);

        if (index is -1)
        {
            // Display the landing page when there is no query following the tagging.
            Host.MarkupLine($"Using the agent [green]{targetAgent.Impl.Name}[/]:");
            targetAgent.Display(Host);

            return null;
        }

        return input[index..].TrimStart();
    }

    private void IgnoreStaleClipboardContent()
    {
        string copiedText = Clipboard.GetText().Trim();
        if (!string.IsNullOrEmpty(copiedText))
        {
            _textToIgnore.Add(copiedText);
        }
    }

    private async Task<string> GetClipboardContent(string input)
    {
        if (!_setting.UseClipboardContent)
        {
            // Skip it when this feature is disabled.
            return null;
        }

        string copiedText = Clipboard.GetText().Trim();
        if (string.IsNullOrEmpty(copiedText) || _textToIgnore.Contains(copiedText))
        {
            return null;
        }

        // Always avoid asking for the same copied text.
        _textToIgnore.Add(copiedText);
        if (input.Contains(copiedText))
        {
            return null;
        }

        // If clipboard content was copied from the code from the last response (whole or partial)
        // by mouse clicking, we should ignore it and don't show the prompt.
        if (GetCodeBlockFromLastResponse() is List<CodeBlock> codeBlocks)
        {
            foreach (CodeBlock code in codeBlocks)
            {
                if (Utils.Contains(code.Code, copiedText))
                {
                    return null;
                }
            }
        }

        string textToShow = copiedText;
        if (copiedText.Length > 150)
        {
            int index = 149;
            while (index < copiedText.Length && !char.IsWhiteSpace(copiedText[index]))
            {
                index++;
            }

            textToShow = copiedText[..index].TrimEnd() + " <more...>";
        }

        Host.RenderReferenceText("clipboard content", textToShow);
        bool confirmed = await Host.PromptForConfirmationAsync(
            prompt: "Include the clipboard content as context information for your query?",
            defaultValue: true);

        return confirmed ? copiedText : null;
    }

    private string GetOneRemoteQuery(out CancellationToken readlineCancelToken)
    {
        readlineCancelToken = CancellationToken.None;
        if (Channel is null)
        {
            return null;
        }

        lock (Channel)
        {
            if (Channel.TryDequeueQuery(out string remoteQuery))
            {
                return remoteQuery;
            }

            readlineCancelToken = Channel.ReadLineCancellationToken;
            return null;
        }
    }

    private async Task<string> ReadUserInput(string prompt, CancellationToken cancellationToken)
    {
        string newLine = Console.CursorLeft is 0 ? string.Empty : "\n";
        Host.Markup($"{newLine}[bold green]{prompt}[/]> ");
        string input = PSConsoleReadLine.ReadLine(cancellationToken);

        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (!input.Contains(' '))
        {
            foreach (var name in CommandRunner.Commands.Keys)
            {
                if (string.Equals(input, name, StringComparison.OrdinalIgnoreCase))
                {
                    string command = $"/{name}";
                    bool confirmed = await Host.PromptForConfirmationAsync(
                        $"Do you mean to run the command {Formatter.Command(command)} instead?",
                        defaultValue: true,
                        cancellationToken: CancellationToken.None);

                    if (confirmed)
                    {
                        input = command;
                    }

                    break;
                }
            }
        }

        return input;
    }

    /// <summary>
    /// Run a chat REPL.
    /// </summary>
    internal async Task RunREPLAsync()
    {
        IgnoreStaleClipboardContent();

        while (!Exit)
        {
            string input = null;
            LLMAgent agent = ActiveAgent;

            try
            {
                if (Regenerate)
                {
                    input = LastQuery;
                    Regenerate = false;
                }
                else
                {
                    input = GetOneRemoteQuery(out CancellationToken readlineCancelToken);
                    if (input is not null)
                    {
                        // Write out the remote query, in the same style as user typing.
                        Host.Markup($"\n>> Remote Query Received:\n");
                        Host.MarkupLine($"[teal]{input}[/]");
                    }
                    else
                    {
                        input = await ReadUserInput(agent?.Prompt ?? Utils.DefaultPrompt, readlineCancelToken);
                        if (input is null)
                        {
                            continue;
                        }
                    }
                }

                if (input.StartsWith('/'))
                {
                    RunCommand(input);
                    continue;
                }

                if (input.StartsWith('@'))
                {
                    input = TaggingAgent(input, ref agent);
                    if (input is null)
                    {
                        continue;
                    }
                }

                string copiedText = await GetClipboardContent(input);
                if (copiedText is not null)
                {
                    input = string.Concat(input, "\n\n", copiedText);
                }

                // Now it's a query to the LLM.
                if (agent is null)
                {
                    // No agent to serve the query. Print the warning and go back to read-line prompt.
                    string agentCommand = Formatter.Command($"/agent use");
                    string helpCommand = Formatter.Command("/help");

                    Host.WriteLine()
                        .MarkupWarningLine("No active agent selected, chat is disabled.")
                        .MarkupWarningLine($"Run {agentCommand} to select an agent, or {helpCommand} for more instructions.")
                        .WriteLine();
                    continue;
                }

                if (agent.IsOrchestrator(out IOrchestrator orchestrator)
                    && _activeAgentStack.Count is 1
                    && !agent.OrchestratorRoleDisabled
                    && _agents.Count > 1)
                {
                    Host.MarkupLine($"The active agent [green]{agent.Impl.Name}[/] can act as an orchestrator and there are multiple agents available.");
                    bool confirmed = await Host.PromptForConfirmationAsync(
                        prompt: "Do you want it to find the most suitable agent for your query?",
                        defaultValue: false);

                    if (confirmed)
                    {
                        List<string> descriptions = new(capacity: _agents.Count);
                        foreach (LLMAgent item in _agents)
                        {
                            descriptions.Add(item.Impl.Description);
                        }

                        try
                        {
                            Task<int> find_agent_op() => orchestrator.FindAgentForPrompt(
                                prompt: input,
                                agents: descriptions,
                                token: CancellationToken).WaitAsync(CancellationToken);

                            int selected = await Host.RunWithSpinnerAsync(find_agent_op, status: "Thinking...");
                            string agentCommand = Formatter.Command($"/agent pop");

                            if (selected >= 0)
                            {
                                var selectedAgent = _agents[selected];
                                PushActiveAgent(selectedAgent);
                                Host.MarkupNoteLine($"Selected agent: [green]{selectedAgent.Impl.Name}[/]")
                                    .MarkupNoteLine($"It's now active for your query. When you are done with the topic, run {agentCommand} to return to the orchestrator.");
                            }
                            else
                            {
                                PushActiveAgent(agent);
                                Host.MarkupNoteLine($"No suitable agent was found. The active agent [green]{agent.Impl.Name}[/] will be used for the topic.")
                                    .MarkupNoteLine($"When you are done with the topic, run {agentCommand} to return to the orchestrator.");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException)
                            {
                                // User cancelled the operation, so we return to the read-line prompt.
                                continue;
                            }

                            agent.OrchestratorRoleDisabled = true;
                            Host.WriteLine()
                                .WriteErrorLine($"Operation failed: {ex.Message}")
                                .WriteLine()
                                .MarkupNoteLine($"The orchestrator role is disabled due to the failure. Continue with the active agent [green]{agent.Impl.Name}[/] for the query.");
                        }
                    }
                }

                try
                {
                    // Use the current active agent for the query.
                    agent = ActiveAgent;
                    LastQuery = input;
                    LastAgent = agent;

                    // TODO: Consider `WaitAsync(CancellationToken)` to handle an agent not responding to ctr+c.
                    // One problem to use `WaitAsync` is to make sure we give reasonable time for the agent to handle the cancellation.
                    bool wasQueryServed = await agent.Impl.Chat(input, this);
                    if (!wasQueryServed)
                    {
                        Host.WriteLine()
                            .MarkupWarningLine($"[[{Utils.AppName}]]: Agent self-check failed. Resolve the issue as instructed and try again.")
                            .MarkupWarningLine($"[[{Utils.AppName}]]: Run {Formatter.Command($"/agent config {agent.Impl.Name}")} to edit the settings for the agent.");
                    }
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                    {
                        // User cancelled the operation, so we return to the read-line prompt.
                        continue;
                    }

                    Host.WriteErrorLine()
                        .WriteErrorLine($"Agent failed to generate a response: {ex.Message}")
                        .WriteErrorLine();
                }
            }
            catch (AIShellException e)
            {
                Host.WriteErrorLine(e.Message);
                if (e.HandlerAction is ExceptionHandlerAction.Stop)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Run a one-time chat.
    /// </summary>
    /// <param name="prompt"></param>
    internal async Task RunOnceAsync(string prompt)
    {
        if (ActiveAgent is null)
        {
            Host.WriteErrorLine("No active agent was configured.");
            Host.WriteErrorLine($"Run '{Utils.AppName} --settings' to configure the active agent. Run '{Utils.AppName} --help' for details.");

            return;
        }

        try
        {
            await ActiveAgent.Impl.Chat(prompt, this).WaitAsync(CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Host.WriteErrorLine("Operation was aborted.");
        }
        catch (AIShellException exception)
        {
            Host.WriteErrorLine(exception.Message);
        }
    }
}
