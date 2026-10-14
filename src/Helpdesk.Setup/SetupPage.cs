using Helpdesk.AgentClient;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Helpdesk.Setup;

/// <summary>A reactive declarative wizard with ordinary Tab focus inside each form.</summary>
public sealed class SetupPage : ReactivePage<SetupViewModel>
{
    private readonly SetupOptions _options;
    private readonly Dictionary<ReactiveProperty<string>, TextInputNode> _inputs = [];
    private readonly CompositeDisposable _layoutWiring = new();
    private readonly List<TextInputNode> _activeFields = [];
    private ScrollableContainerNode? _scroll;
    private ModalNode? _modal;

    public SetupPage(SetupOptions options) { _options = options; FocusPolicy = FocusPolicy.FirstFocusable; }
    private Color Accent => _options.Monochrome ? Color.Default : Color.Cyan;

    protected override void OnBound()
    {
        foreach (var field in new[] { ViewModel.Profile, ViewModel.Instance, ViewModel.ManualSecret, ViewModel.TokenUrl, ViewModel.ClientId,
            ViewModel.Username, ViewModel.Scope, ViewModel.SecretFile, ViewModel.InstanceLabel, ViewModel.HostPublicOrigin, ViewModel.ApiBackend,
            ViewModel.McpBackend, ViewModel.AllowedOrigins, ViewModel.Listener, ViewModel.Authority, ViewModel.Audience,
            ViewModel.RequiredScopes, ViewModel.RequiredGroups, ViewModel.OutputDirectory })
        {
            var input = new TextInputNode().WithMaxLength(field == ViewModel.ManualSecret ? 8192 : 2048);
            if (field == ViewModel.ManualSecret) input.AsPassword('*');
            _inputs[field] = input;
            input.TextChanged.Subscribe(value =>
            {
                var safe = field == ViewModel.ManualSecret ? value : TerminalText.Sanitize(value);
                if (input.Text != safe) input.Text = safe;
                field.Value = safe;
            }).DisposeWith(Subscriptions);
            field.Subscribe(value => { if (input.Text != value) input.Text = value; }).DisposeWith(Subscriptions);
        }
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        KeyBindings.Register(ConsoleKey.Tab, () => { CycleFocusForward(); RevealFocus(); });
        KeyBindings.Register(ConsoleKey.Tab, ConsoleModifiers.Shift, () => { CycleFocusBackward(); RevealFocus(); });
        KeyBindings.Register(ConsoleKey.N, ConsoleModifiers.Control, () => { if (_modal is null) _ = ViewModel.NextAsync(); });
        KeyBindings.Register(ConsoleKey.B, ConsoleModifiers.Control, ViewModel.Back);
        KeyBindings.Register(ConsoleKey.O, ConsoleModifiers.Control, () => _ = ViewModel.OpenBrowserAsync());
        KeyBindings.Register(ConsoleKey.C, ConsoleModifiers.Control, () => _ = ViewModel.CancelAsync());
        KeyBindings.Register(ConsoleKey.Escape, () =>
        {
            if (ViewModel.ConfirmOverwrite.Value) ViewModel.ConfirmOverwrite.Value = false;
            else if (ViewModel.Step.Value == SetupStep.Complete) ViewModel.RequestShutdown();
            else _ = ViewModel.CancelAsync();
        });
        KeyBindings.Register(ConsoleKey.F2, ViewModel.ToggleAdvanced);
        KeyBindings.Register(ConsoleKey.PageDown, () => _scroll?.PageDown());
        KeyBindings.Register(ConsoleKey.PageUp, () => _scroll?.PageUp());
        ViewModel.Step.Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        ViewModel.TerminalWidth.DistinctUntilChanged().Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        ViewModel.Advanced.Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        ViewModel.ConfirmOverwrite.Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
    }

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter && _modal is null && (Focus.CurrentFocus is TextInputNode || ViewModel.Step.Value is SetupStep.Review or SetupStep.Complete))
        { _ = ViewModel.NextAsync(); return true; }
        if (_modal is not null && keyInfo.Key == ConsoleKey.Tab) return true;
        return base.HandlePageInput(keyInfo);
    }

    private void RevealFocus()
    {
        var index = _activeFields.FindIndex(field => ReferenceEquals(field, Focus.CurrentFocus));
        if (index >= 0) _scroll?.ScrollTo(Math.Max(0, index * 3 - 3));
    }

    public override ILayoutNode BuildLayout()
    {
        _layoutWiring.Clear();
        _activeFields.Clear();
        var content = BuildStep();
        _scroll = new ScrollableContainerNode().WithContent(content).WithAutoScroll(AutoScrollPolicy.None).WithScrollbar(true);
        var form = new PanelNode().WithTitle(Title()).WithBorder(BorderStyle.Single).WithBorderColor(Accent).WithContent(_scroll);
        LayoutNode body = ViewModel.TerminalWidth.Value >= 104
            ? Layouts.Horizontal(new TextNode(Stepper()).WithForeground(Accent).Width(20), form.WidthFill())
            : form;
        var header = Layouts.Vertical(
            new TextNode("RatelDesk setup").WithForeground(Accent).Bold().Height(1),
            new TextNode($"{Title()} · profile {TerminalText.Sanitize(ViewModel.Profile.Value, 64)} · {TerminalText.Sanitize(ViewModel.Instance.Value, 100)}").Height(1));
        var status = ViewModel.Status.Select<string, ILayoutNode>(message => new TextNode(TerminalText.Sanitize(message)).WithForeground(Accent)).AsLayout(RenderFrameProvider);
        var working = ViewModel.Busy.Select<bool, ILayoutNode>(busy => busy
            ? new SpinnerNode().WithLabel("Working…").WithSpinnerColor(Accent)
            : new TextNode("[PageUp/PageDown] Scroll · externally managed settings are read-only")).AsLayout(RenderFrameProvider);
        var root = Layouts.Vertical(header.Height(2), body.Fill(), status.Height(2), working.Height(1), new TextNode(ViewModel.Footer).Height(2));
        if (!ViewModel.ConfirmOverwrite.Value)
        {
            if (_modal is not null) { Focus.PopFocus(); _modal = null; }
            return root;
        }
        var choice = Layouts.SelectionList("Keep existing configuration", "Save reviewed replacement").WithVisibleRows(2).WithHighlightColors(Color.Default, _options.Monochrome ? Color.Default : Color.Blue);
        choice.SelectionConfirmed.Subscribe(selected =>
        {
            ViewModel.ConfirmOverwrite.Value = false;
            if (selected.FirstOrDefault() == "Save reviewed replacement") _ = ViewModel.NextAsync(overwriteConfirmed: true);
        }).DisposeWith(_layoutWiring);
        if (_modal is not null) Focus.PopFocus();
        _modal = new ModalNode().WithTitle("Confirm replacement").WithContent(choice)
            .WithFooter("Existing secrets remain hidden. [Esc] Keep existing configuration")
            .WithBorder(BorderStyle.Single).WithBorderColor(Accent).WithDismissOnEscape(false);
        Focus.PushFocus(_modal);
        return Layouts.Stack(root, _modal);
    }

    private string Title() => ViewModel.Step.Value switch
    {
        SetupStep.Purpose => "Choose a task", SetupStep.Profile => "Client and profile", SetupStep.Instance => "Trusted instance",
        SetupStep.Authentication => "Authentication", SetupStep.Approval => "Browser approval", SetupStep.Hosting => "HTTP MCP hosting",
        SetupStep.Review => "Review and save", SetupStep.Checks => "Read-only checks", _ => "Result and next steps"
    };
    private string Stepper() => string.Join('\n', new[]
    {
        (SetupStep.Purpose, "Task"), (SetupStep.Profile, "Client / profile"), (SetupStep.Instance, "Instance"),
        (SetupStep.Authentication, "Authentication"), (SetupStep.Approval, "Approval"), (SetupStep.Hosting, "Hosting"),
        (SetupStep.Review, "Review"), (SetupStep.Checks, "Checks"), (SetupStep.Complete, "Done")
    }.Where(item => ViewModel.IsHosting ? item.Item1 is SetupStep.Purpose or SetupStep.Hosting or SetupStep.Review or SetupStep.Complete : item.Item1 != SetupStep.Hosting)
        .Select(item => $"{(ViewModel.Step.Value == item.Item1 ? ">" : " ")} {item.Item2}"));

    private ILayoutNode BuildStep()
    {
        return ViewModel.Step.Value switch
        {
            SetupStep.Purpose => Layouts.Vertical(
                new TextNode("Connect a client, or export administrator/deployment settings. Local export grants no remote privileges.").HeightAuto(),
                Select(["Connect a client", "Configure HTTP MCP hosting"], ViewModel.IsHosting ? 1 : 0, value => ViewModel.SelectPurpose(value == "Configure HTTP MCP hosting"))),
            SetupStep.Profile => Layouts.Vertical(
                new TextNode("Profiles isolate authorized organizations and credential purposes.").HeightAuto(),
                Select(["CLI", "stdio MCP", "HTTP MCP client"], (int)ViewModel.Transport, value => ViewModel.SelectTransport(value == "CLI" ? SetupTransport.Cli : value == "stdio MCP" ? SetupTransport.Stdio : SetupTransport.Http)),
                Input("Profile name", ViewModel.Profile, "lowercase letters, digits and hyphens"),
                ViewModel.Transport == SetupTransport.Stdio ? Input("Instance label", ViewModel.InstanceLabel, "local") : Layouts.Empty()),
            SetupStep.Instance => Layouts.Vertical(
                new TextNode("Use the public HTTPS origin. Discovery sends no existing credential and never guesses another instance.").HeightAuto(),
                Input("Instance URL", ViewModel.Instance, "https://desk.example.com", "apiBaseUrl")),
            SetupStep.Authentication => AuthenticationForm(),
            SetupStep.Hosting => HostingForm(),
            _ => ViewModel.Detail.Select<string, ILayoutNode>(text => new TextNode(text)).AsLayout(RenderFrameProvider)
        };
    }

    private ILayoutNode AuthenticationForm()
    {
        var choices = ViewModel.Transport == SetupTransport.Http
            ? new[] { "Browser approval", "Import existing MCP credential", "Keep existing / externally managed identity", "External Authentik / OAuth client" }
            : new[] { "Browser approval", "Import existing API credential", "Keep existing / externally managed identity", "External Authentik identity" };
        var index = ViewModel.Authentication switch { "manual" => 1, "existing" => 2, "authentik" or "oauth" => 3, _ => 0 };
        var identity = Select(choices, index, value =>
        {
            ViewModel.SelectAuthentication(value.StartsWith("Browser", StringComparison.Ordinal) ? "browser" : value.StartsWith("Import", StringComparison.Ordinal) ? "manual" : value.StartsWith("Keep", StringComparison.Ordinal) ? "existing" : ViewModel.Transport == SetupTransport.Http ? "oauth" : "authentik");
            InvalidateLayout();
        });
        var fields = Layouts.Vertical(new TextNode(ViewModel.Detail.Value).HeightAuto(), identity);
        if (ViewModel.Authentication == "manual")
            fields.WithChild(Input("Credential (masked)", ViewModel.ManualSecret, "rdk_…", "integrationCredential"))
                .WithChild(Input("Or private credential file", ViewModel.SecretFile, "leave blank when entering a credential", "integrationCredentialFile"));
        if (ViewModel.Authentication == "authentik")
            fields.WithChild(new TextNode("External Authentik is a separately configured identity. This does not grant per-user MCP delegation.").HeightAuto())
                .WithChild(Input("Token URL", ViewModel.TokenUrl, "https://auth.example.com/application/o/token/", "authentikTokenUrl"))
                .WithChild(Input("Client id", ViewModel.ClientId, "", "authentikClientId"))
                .WithChild(Input("Service username", ViewModel.Username, "", "authentikUsername"))
                .WithChild(Input("App password (masked)", ViewModel.ManualSecret, "", "authentikAppPassword"))
                .WithChild(Input("Or private password file", ViewModel.SecretFile, "", "authentikAppPasswordFile"))
                .WithChild(ViewModel.Advanced.Value ? Input("Scope", ViewModel.Scope, AgentClientConfiguration.DefaultScope, "authentikScope") : Layouts.Empty());
        if (ViewModel.Authentication == "existing")
            fields.WithChild(new TextNode("The existing identity is retained only for its reviewed target. Environment/read-only sources are externally managed.").HeightAuto());
        if (ViewModel.Authentication == "oauth")
            fields.WithChild(new TextNode("Your OAuth-capable MCP client owns login and token storage. Setup verifies the canonical protected-resource metadata and saves only the reviewed resource/HTTPS authorities and optional imported public client ID.").HeightAuto())
                .WithChild(ViewModel.Advanced.Value ? Input("Imported public OAuth client ID (optional)", ViewModel.ClientId, "No client secret is collected") : Layouts.Empty());
        return fields;
    }

    private ILayoutNode HostingForm()
    {
        var fields = Layouts.Vertical(new TextNode("Configure the public resource and private backends separately. This writes an export for operator apply/recreation.").HeightAuto(),
            Input("Export directory", ViewModel.OutputDirectory, "persistent writable mount"),
            Select(["Load existing export", "Edit reviewed settings"], 1, value => { if (value == "Load existing export") { try { ViewModel.LoadHosting(); } catch (AgentClientValidationException exception) { ViewModel.Status.Value = TerminalText.Sanitize(exception.Message); } InvalidateLayout(); } }),
            Input("Public HTTPS origin", ViewModel.HostPublicOrigin, "https://desk.example.com", "PublicOrigin"),
            Input("Private API backend", ViewModel.ApiBackend, "http://api:8222/", "ApiBaseUrl"),
            Input("Private MCP destination", ViewModel.McpBackend, "http://mcp-http:8223/", "McpDestination"),
            Input("Instance label", ViewModel.InstanceLabel, "local", "Instance"),
            ViewModel.HostManaged("AuthenticationMode") ? new TextNode($"Ingress mode: {ViewModel.HostAuthentication} · externally managed ({ViewModel.HostManagedSource("AuthenticationMode")})").HeightAuto() :
                Select(["Gateway (per-request delegation)", "External Authentik ingress"], ViewModel.HostAuthentication == "gateway" ? 0 : 1,
                    value => { ViewModel.SelectHostAuthentication(value.StartsWith("Gateway", StringComparison.Ordinal) ? "gateway" : "authentik"); InvalidateLayout(); }));
        if (ViewModel.Advanced.Value)
            fields.WithChild(Input("Allowed browser origins (comma separated)", ViewModel.AllowedOrigins, "defaults to public origin", "AllowedOrigins"))
                .WithChild(Input("Listener", ViewModel.Listener, "http://+:8223", "Listener"));
        if (ViewModel.HostAuthentication == "authentik")
        {
            fields.WithChild(new TextNode("[F2] Advanced fields configure Authentik ingress and its separate downstream API identity. Gateway requires neither deployment secret nor shared signing key.").HeightAuto());
            if (ViewModel.Advanced.Value)
                fields.WithChild(Input("Authentik authority", ViewModel.Authority, "https://auth.example.com/application/o/mcp/", "AuthentikAuthority"))
                    .WithChild(Input("MCP audience", ViewModel.Audience, "public origin + /mcp", "AuthentikAudience"))
                    .WithChild(Input("Required scopes", ViewModel.RequiredScopes, "comma separated", "RequiredScopes"))
                    .WithChild(Input("Required groups", ViewModel.RequiredGroups, "comma separated", "RequiredGroups"))
                    .WithChild(ViewModel.HostManaged("Outbound") ? new TextNode($"Downstream identity · externally managed ({ViewModel.HostManagedSource("Outbound")})").HeightAuto() :
                        Select(["Keep existing downstream identity", "Configure downstream Authentik identity", "Import downstream API credential file"], ViewModel.DownstreamAuthentication switch { "existing" => 0, "integration" => 2, _ => 1 },
                            value => { ViewModel.SelectDownstreamAuthentication(value.StartsWith("Keep", StringComparison.Ordinal) ? "existing" : value.StartsWith("Import", StringComparison.Ordinal) ? "integration" : "authentik"); InvalidateLayout(); }))
                    .WithChild(ViewModel.DownstreamAuthentication == "existing" ? new TextNode("Existing private downstream identity stays masked and bound to its reviewed API backend.").HeightAuto() : Layouts.Empty())
                    .WithChild(Input("Downstream token URL", ViewModel.TokenUrl, "https://auth.example.com/application/o/token/", "Outbound"))
                    .WithChild(Input("Downstream client id", ViewModel.ClientId, "", "Outbound"))
                    .WithChild(Input("Downstream service username", ViewModel.Username, "", "Outbound"))
                    .WithChild(Input("Downstream private secret file", ViewModel.SecretFile, "deployment-owned private secret reference", "Outbound"));
        }
        return fields;
    }

    private ILayoutNode Input(string label, ReactiveProperty<string> field, string placeholder, string? setting = null)
    {
        if (setting is not null && (ViewModel.IsHosting ? ViewModel.HostManaged(setting) : ViewModel.Managed(setting)))
            return Layouts.Vertical(new TextNode($"{label} · externally managed ({(ViewModel.IsHosting ? ViewModel.HostManagedSource(setting) : ViewModel.ManagedSource(setting))})").WithForeground(Accent).Height(1),
                new TextNode(field == ViewModel.ManualSecret || field == ViewModel.SecretFile || setting.Contains("Credential", StringComparison.OrdinalIgnoreCase) || setting.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    ? "[private external source]" : TerminalText.Sanitize(field.Value)).Height(1), new TextNode("").Height(1));
        var input = _inputs[field];
        input.WithPlaceholder(placeholder).WithForeground(_options.Monochrome ? Color.Default : Color.White);
        _activeFields.Add(input);
        return Layouts.Vertical(new TextNode(label).WithForeground(Accent).Height(1), input.Height(1), new TextNode("").Height(1));
    }
    private ILayoutNode Select(string[] choices, int selectedIndex, Action<string> changed)
    {
        var list = Layouts.SelectionList(choices).WithMode(SelectionMode.Single).WithHighlightedIndex(Math.Clamp(selectedIndex, 0, choices.Length - 1))
            .WithVisibleRows(choices.Length).WithHighlightColors(Color.Default, _options.Monochrome ? Color.Default : Color.Blue);
        list.SelectionConfirmed.Subscribe(selected => { if (selected.Count > 0) changed(selected[0]); }).DisposeWith(_layoutWiring);
        return list;
    }
    public override void Dispose() { _layoutWiring.Dispose(); base.Dispose(); }
}
