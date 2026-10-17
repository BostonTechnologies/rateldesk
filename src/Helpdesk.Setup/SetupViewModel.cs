using System.Text.Json;
using Helpdesk.AgentClient;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Helpdesk.Setup;

public enum SetupApprovalContext { Client, Host, Verification }

public sealed class SetupViewModel : ReactiveViewModel
{
    private readonly ISetupWorkflow _workflow;
    private readonly SetupOptions _options;
    private CancellationTokenSource _operation = new();
    private long _generation;
    private PairingSession? _session;
    private string? _sessionOrigin;
    private PairedIntegrationCredential? _credential;
    private SetupProfile? _profile;
    private string? _loadedProfile;
    private AgentClientConfiguration? _review;
    private bool _acknowledged;
    private bool _saved;
    private ConfigurationProfileSaveResult? _savedProfile;
    private PendingPairingProfile? _pending;
    private DateTimeOffset? _deliveryExpires;
    private bool _disposed;
    private bool _closing;
    private bool _normalizingInput;
    private bool _useNativeApiTarget;
    private readonly List<Task> _cleanup = [];
    private HttpMcpHostingSettings _hosting = new();
    private HttpMcpDeploymentProposal? _hostProposal;
    private IHostDeploymentWorkflow? _hostWorkflow;
    private readonly Func<string, IHostDeploymentWorkflow> _hostWorkflowFactory;
    private readonly Func<string, IHostVerificationWorkflow> _verificationFactory;
    private IHostVerificationWorkflow? _verifier;
    private PairingSession? _verificationSession;
    private Task? _verificationPolling;
    private PairingSession? _hostSession;
    private bool _hostApproved;
    private IReadOnlyDictionary<string, string> _hostManaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SetupViewModel(ISetupWorkflow workflow, SetupOptions options,
        Func<string, IHostDeploymentWorkflow>? hostWorkflowFactory = null,
        Func<string, IHostVerificationWorkflow>? verificationFactory = null)
    {
        _workflow = workflow;
        _hostWorkflowFactory = hostWorkflowFactory ?? (origin => new HostDeploymentWorkflow(origin));
        _verificationFactory = verificationFactory ?? (directory => new HostVerificationWorkflow(directory));
        _options = options;
        Transport = options.Transport;
        IsHosting = options.Hosting;
        Profile.Value = options.Profile;
        OutputDirectory.Value = options.OutputDirectory ?? (options.Hosting ? SetupInput.HostDirectory : "");
        if (options.VerificationDirectory is not null) { IsHosting = true; ExitCode = 1; }
        else if (options.Hosting) { LoadHosting(); Instance.Value = HostPublicOrigin.Value; }
        else LoadClientProfile();
        if (options.InstanceLabel is not null) InstanceLabel.Value = options.InstanceLabel;
        else if (!options.Hosting && options.Transport == SetupTransport.Stdio && Environment.GetEnvironmentVariable("RATELDESK_MCP_INSTANCE") is { Length: > 0 } configuredInstance)
            InstanceLabel.Value = configuredInstance;
        if (options.WebUrl is not null) Instance.Value = options.WebUrl;
        if (options.Authentication is not null) Authentication = options.Authentication;
        Step.Value = options.VerificationDirectory is null ? SetupStep.Instance : SetupStep.Verification;
        Status.Value = options.VerificationDirectory is null ? "Enter your RatelDesk Web URL. [Enter] Next · [F2] Advanced"
            : "Applied—verify pending. Next checks the saved generation and requests temporary status consent.";
        Instance.Skip(1).Subscribe(_ => InvalidateTarget()).DisposeWith(Subscriptions);
        Profile.Skip(1).Subscribe(_ => InvalidateTarget()).DisposeWith(Subscriptions);
        NativeApiTarget.Skip(1).Subscribe(_ => InvalidateTarget()).DisposeWith(Subscriptions);
        if (options.VerificationDirectory is null)
            foreach (var field in new[] { HostPublicOrigin, ApiBackend, McpBackend, AllowedOrigins, Listener, Authority, Audience,
                RequiredScopes, RequiredGroups, OutputDirectory, InstanceLabel, TokenUrl, ClientId, Username, Scope })
                field.Skip(1).DistinctUntilChanged().Subscribe(_ => { if (IsHosting) InvalidateTarget(); }).DisposeWith(Subscriptions);
    }

    public ReactiveProperty<SetupStep> Step { get; } = new(SetupStep.Instance);
    public ReactiveProperty<string> Status { get; } = new("Choose a setup task. All checks are read-only.");
    public ReactiveProperty<string> Detail { get; } = new("");
    public ReactiveProperty<bool> Busy { get; } = new(false);
    public ReactiveProperty<bool> ConfirmOverwrite { get; } = new(false);
    public ReactiveProperty<bool> Advanced { get; } = new(false);
    public ReactiveProperty<int> TerminalWidth { get; } = new(80);
    public ReactiveProperty<int> TerminalHeight { get; } = new(24);
    public ReactiveProperty<bool> DetailsVisible { get; } = new(false);
    public SetupApprovalContext ApprovalContext { get; private set; }
    public int ExitCode { get; private set; }
    public string ApprovalTitle => ApprovalContext switch { SetupApprovalContext.Host => "Host deployment approval", SetupApprovalContext.Verification => "Temporary status verification", _ => "Client access approval" };
    public string ApprovalPurpose => ApprovalContext switch
    {
        SetupApprovalContext.Host => "Review this deployment; no business access is issued.",
        SetupApprovalContext.Verification => "Status only; temporary access is cleaned up after verification.",
        _ => $"{(Transport == SetupTransport.Http ? "MCP" : "API")} access; choose organization and exact permissions in the browser."
    };
    public string ApprovalExpiry => $"Expires {(ApprovalContext == SetupApprovalContext.Verification ? _verificationSession : ApprovalContext == SetupApprovalContext.Host ? _hostSession : _session)?.ExpiresAtUtc:HH:mm:ss 'UTC'}";
    public string ApprovalInstance => TerminalText.Sanitize(ApprovalContext == SetupApprovalContext.Client ? Instance.Value : HostPublicOrigin.Value);
    public string ApprovalSummary { get; private set; } = "";
    public ReactiveProperty<string> Profile { get; } = new("default");
    public ReactiveProperty<string> Instance { get; } = new("");
    public ReactiveProperty<string> NativeApiTarget { get; } = new("");
    public ReactiveProperty<string> ManualSecret { get; } = new("");
    public ReactiveProperty<string> TokenUrl { get; } = new("");
    public ReactiveProperty<string> ClientId { get; } = new("");
    public ReactiveProperty<string> Username { get; } = new("");
    public ReactiveProperty<string> Scope { get; } = new(AgentClientConfiguration.DefaultScope);
    public ReactiveProperty<string> SecretFile { get; } = new("");
    public ReactiveProperty<string> InstanceLabel { get; } = new("local");
    public ReactiveProperty<string> HostPublicOrigin { get; } = new("");
    public ReactiveProperty<string> ApiBackend { get; } = new("http://api:8222/");
    public ReactiveProperty<string> McpBackend { get; } = new("http://mcp-http:8223/");
    public ReactiveProperty<string> AllowedOrigins { get; } = new("");
    public ReactiveProperty<string> Listener { get; } = new("http://+:8223");
    public ReactiveProperty<string> Authority { get; } = new("");
    public ReactiveProperty<string> Audience { get; } = new("");
    public ReactiveProperty<string> RequiredScopes { get; } = new("mcp");
    public ReactiveProperty<string> RequiredGroups { get; } = new("rateldesk-mcp");
    public ReactiveProperty<string> OutputDirectory { get; } = new("");
    public bool IsHosting { get; private set; }
    public SetupTransport Transport { get; private set; }
    public string Authentication { get; private set; } = "browser";
    public string HostAuthentication { get; private set; } = "gateway";
    public string DownstreamAuthentication { get; private set; } = "authentik";
    public bool PairingSupported { get; private set; }
    public string? BrowserUrl => _verificationSession?.VerificationUri ?? _hostSession?.VerificationUri ?? _session?.VerificationUri;
    public string? HumanCode => _verificationSession?.HumanCode ?? _hostSession?.HumanCode ?? _session?.HumanCode;
    public string? ProfilePath => _profile?.Resolution.ProfilePath;
    public string Footer => Step.Value == SetupStep.Complete ? "[Enter/Esc] Exit" : Step.Value == SetupStep.Approval
        ? "[Ctrl+O] Open · [Ctrl+Y] Copy · [Ctrl+D] Details · [Ctrl+B] Back\n[Tab/Shift+Tab] Focus · [Enter] Action · [PgUp/PgDn] Scroll · [Esc] Cancel" : "[Enter/Ctrl+N] Next   [Ctrl+B] Back   [Esc] Cancel   [F2] Advanced\n[Tab/Shift+Tab] Focus · [PageUp/PageDown] Scroll";
    public bool Managed(string key) => _profile?.Resolution.Settings.Any(setting => setting.Managed && setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true;
    public string ManagedSource(string key) => _profile?.Resolution.Settings.FirstOrDefault(setting => setting.Managed && setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Source ?? "external configuration";
    public bool HostManaged(string key) => _hostManaged.ContainsKey(key);
    public string HostManagedSource(string key) => _hostManaged.TryGetValue(key, out var source) ? source : "external configuration";

    public override void OnActivated()
    {
        Input.OfType<IInputEvent, ResizeEvent>().Subscribe(resize => { TerminalWidth.Value = resize.Width; TerminalHeight.Value = resize.Height; }).DisposeWith(Subscriptions);
        if (_options.VerificationDirectory is not null) _ = NextAsync();
    }

    public void SelectPurpose(bool hosting)
    {
        if (IsHosting != hosting)
        {
            InvalidateTarget(); IsHosting = hosting;
            if (hosting)
            {
                if (OutputDirectory.Value.Length == 0) OutputDirectory.Value = SetupInput.HostDirectory;
                LoadHosting(); if (Instance.Value.Length == 0) Instance.Value = HostPublicOrigin.Value;
            }
            else LoadClientProfile(preserveUrl: true);
        }
        Status.Value = hosting ? "Configure this MCP server; personal client profiles are separate." : "Connect a personal client profile.";
    }
    public void SelectTransport(SetupTransport transport)
    {
        if (Transport != transport) { InvalidateTarget(); Transport = transport; LoadClientProfile(preserveUrl: true); }
        Status.Value = $"Selected {transport.ToString().ToLowerInvariant()} client.";
    }
    public void SelectAuthentication(string authentication) { Authentication = authentication; Status.Value = authentication == "browser" ? "The signed-in browser selects the actual grants." : "Use an existing credential or explicit external identity."; }
    public void SelectHostAuthentication(string mode) { if (HostAuthentication != mode) InvalidateTarget(); HostAuthentication = mode; Status.Value = mode == "gateway" ? "Gateway mode has no stored downstream deployment credential." : "Authentik ingress and downstream API identity are separate configurations."; }
    public void SelectDownstreamAuthentication(string mode) { if (DownstreamAuthentication != mode) InvalidateTarget(); DownstreamAuthentication = mode; Status.Value = "Downstream identity is a separate deliberate choice. Existing identities stay bound to their reviewed API backend."; }
    public void ToggleAdvanced() { Advanced.Value = !Advanced.Value; }
    public void ToggleDetails() { DetailsVisible.Value = !DetailsVisible.Value; }

    private void LoadClientProfile(bool preserveUrl = false)
    {
        _profile = _workflow.Load(Profile.Value, Transport);
        _loadedProfile = Profile.Value;
        var config = _profile.Resolution.Configuration;
        var seed = _options.SeedConfiguration;
        if (!preserveUrl || Instance.Value.Length == 0)
        {
            var configuredUrl = seed?.ApiBaseUrl ?? (_profile.Resolution.Settings.Any(s => s.Key == "apiBaseUrl" && s.Source != "default") ? config.ApiBaseUrl ?? "" : "");
            if (Transport != SetupTransport.Http && configuredUrl.Length > 0)
            {
                try { _ = SetupInput.NormalizeWebUrl(configuredUrl); NativeApiTarget.Value = ""; }
                catch (AgentClientValidationException) { NativeApiTarget.Value = configuredUrl; configuredUrl = ""; }
            }
            Instance.Value = _options.WebUrl ?? configuredUrl;
        }
        TokenUrl.Value = seed?.AuthentikTokenUrl ?? config.AuthentikTokenUrl ?? "";
        ClientId.Value = seed?.AuthentikClientId ?? config.OAuthClientId ?? config.AuthentikClientId ?? "";
        Username.Value = seed?.AuthentikUsername ?? config.AuthentikUsername ?? "";
        Scope.Value = seed?.AuthentikScope ?? config.AuthentikScope ?? AgentClientConfiguration.DefaultScope;
        SecretFile.Value = config.IntegrationCredentialFile ?? config.AuthentikAppPasswordFile ?? "";
        Authentication = _options.Authentication ?? (Transport == SetupTransport.Http && config.HttpClientAuthentication == "oauth" ? "oauth" :
            seed?.AuthentikTokenUrl is not null ? "authentik" : HasExistingIdentity(config) ? "existing" : "browser");
        if (!preserveUrl && _options.WebUrl is null && _profile.Pending is { } pending) Instance.Value = pending.Origin;
    }

    private static bool HasExistingIdentity(AgentClientConfiguration config) =>
        config.IntegrationCredential is not null || config.IntegrationCredentialFile is not null ||
        config.AuthentikAppPassword is not null || config.AuthentikAppPasswordFile is not null;

    private void InvalidateTarget(bool preserveDraft = false)
    {
        if (_hostSession is { } hostSession && _hostWorkflow is { } hostWorkflow && !_saved)
            _cleanup.Add(CancelHostApprovalAsync(hostWorkflow, hostSession));
        _hostSession = null; _hostProposal = null; _hostApproved = false;
        if (_normalizingInput) return;
        if (_verifier is { } verifier)
        { _verifier = null; var polling = _verificationPolling; _verificationPolling = null; _cleanup.Add(DisposeVerifierAsync(verifier, polling)); }
        _verificationSession = null;
        DetailsVisible.Value = false;
        ++_generation;
        _operation.Cancel();
        _operation.Dispose();
        _operation = new();
        Busy.Value = false;
        _credential = null;
        _review = null;
        _deliveryExpires = null;
        if (!preserveDraft && !IsHosting && Step.Value is SetupStep.Instance or SetupStep.Authentication or SetupStep.Approval)
        { ManualSecret.Value = ""; SecretFile.Value = ""; }
        if (_session is { } session && _sessionOrigin is { } origin && !_acknowledged && !_saved)
        {
            _cleanup.Add(CancelDeliveryAsync(origin, session));
        }
        if (_pending is { } pending && !_saved)
        {
            try { _workflow.Discard(pending); }
            catch (AgentClientValidationException exception) { Status.Value = TerminalText.Sanitize(exception.Message); }
        }
        _pending = null; _session = null; _sessionOrigin = null; _acknowledged = false;
        ConfirmOverwrite.Value = false;
    }

    private async Task CancelDeliveryAsync(string origin, PairingSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _workflow.CancelAsync(origin, session, timeout.Token); }
        catch { /* The bounded server expiry remains the cleanup fallback. Never touch an older profile. */ }
    }

    public async Task NextAsync(bool overwriteConfirmed = false)
    {
        if (_closing || Busy.Value) return;
        if (Step.Value == SetupStep.Complete) { Shutdown(); return; }
        if (Step.Value == SetupStep.Approval) { Status.Value = "Wait for browser approval, or cancel. Opening the browser is optional."; return; }
        Busy.Value = true;
        var generation = _generation;
        var token = _operation.Token;
        try
        {
            switch (Step.Value)
            {
                case SetupStep.Verification:
                    await Task.WhenAll(_cleanup);
                    if (token.IsCancellationRequested || generation != _generation) return;
                    _verifier = _verificationFactory(_options.VerificationDirectory!);
                    HostPublicOrigin.Value = _verifier.Origin;
                    if (!_verifier.Supported)
                    { await _verifier.DisposeAsync(); _verifier = null; throw new AgentClientValidationException(HostVerificationWorkflow.ExternalMessage); }
                    var verificationWorkflow = _verifier;
                    var verificationStart = verificationWorkflow.StartAsync(token);
                    _verificationPolling = verificationStart;
                    var verificationSession = await verificationStart;
                    if (token.IsCancellationRequested || generation != _generation) return;
                    _verificationSession = verificationSession;
                    ApprovalContext = SetupApprovalContext.Verification;
                    ApprovalSummary = "Applied host generation retained. No deployment or ordinary client approval is requested.";
                    Detail.Value = ApprovalSummary + "\n" + ApprovalPurpose;
                    Step.Value = SetupStep.Approval;
                    Status.Value = "Temporary status approval pending.";
                    _verificationPolling = PollVerificationAsync(generation, token, verificationSession, verificationWorkflow);
                    break;
                case SetupStep.Purpose:
                    if (IsHosting) LoadHosting();
                    Step.Value = IsHosting ? SetupStep.Hosting : SetupStep.Profile;
                    break;
                case SetupStep.Profile:
                    LoadClientProfile(preserveUrl: true);
                    Step.Value = SetupStep.Instance;
                    Status.Value = "Enter your RatelDesk Web URL. Existing credentials are withheld from discovery.";
                    break;
                case SetupStep.Instance:
                    if (!IsHosting && (_profile is null || _loadedProfile != Profile.Value)) LoadClientProfile(preserveUrl: true);
                    _useNativeApiTarget = !IsHosting && Transport != SetupTransport.Http && Advanced.Value && NativeApiTarget.Value.Length > 0;
                    if (_useNativeApiTarget)
                    {
                        if (Authentication == "browser")
                            throw new AgentClientValidationException("An explicit private API target requires an existing/manual API credential or external Authentik identity in Advanced. Browser approval uses the public Web URL.");
                        var apiTarget = AgentClientConfigurationValidator.ApiUri(NativeApiTarget.Value).AbsoluteUri;
                        if (Managed("apiBaseUrl") && !AgentClientConfigurationValidator.SameTarget(new Uri(_profile!.Resolution.Configuration.ApiBaseUrl!), new Uri(apiTarget)))
                            throw new AgentClientValidationException("The API destination is externally managed. Change its owning source or select an isolated profile.");
                        _workflow.PreflightStorage(Profile.Value, Transport);
                        PairingSupported = false;
                        Detail.Value = "Use the explicitly selected API destination. No browser discovery runs and no existing credential is sent to the Web URL.";
                        Step.Value = SetupStep.Authentication;
                        break;
                    }
                    var origin = SetupInput.NormalizeWebUrl(Instance.Value);
                    if (Managed("apiBaseUrl") && !AgentClientConfigurationValidator.SameTarget(new Uri(_profile!.Resolution.Configuration.ApiBaseUrl!), new Uri(origin)))
                        throw new AgentClientValidationException("The instance URL is externally managed. Change its owning environment, or use a separate environment/profile.");
                    if (Instance.Value != origin)
                    {
                        _normalizingInput = true;
                        try { Instance.Value = origin; }
                        finally { _normalizingInput = false; }
                    }
                    generation = _generation; token = _operation.Token;
                    if (IsHosting)
                    {
                        HostPublicOrigin.Value = origin;
                        SetupInput.CheckPrivateStorage(System.IO.Path.Combine(OutputDirectory.Value, "host-settings.json"));
                        Step.Value = SetupStep.Hosting;
                        Status.Value = "Review the server settings. Advanced shows deployment topology and authentication.";
                        break;
                    }
                    _workflow.PreflightStorage(Profile.Value, Transport);
                    if (_profile!.Pending is { } recovered)
                    {
                        if (!AgentClientConfigurationValidator.SameTarget(new Uri(origin), new Uri(recovered.Origin)))
                            throw new AgentClientValidationException($"The pending approval belongs to {TerminalText.Sanitize(recovered.Origin)}. Restore that Web URL to resume it; Cancel removes an unacknowledged delivery.");
                        generation = _generation; token = _operation.Token;
                        var recoveredConfiguration = _profile.PendingConfiguration;
                        var deliveryState = recovered.Acknowledged ? null : await _workflow.PollAsync(recovered.Origin, recovered.Session, token);
                        if (_closing || token.IsCancellationRequested || generation != _generation) return;
                        if (deliveryState?.Status is "expired" or "cancelled" or "denied")
                        {
                            _workflow.Discard(recovered);
                            await ApplyAsync(generation, token, () => { _pending = null; _session = null; _review = null; Instance.Value = _profile.Resolution.Configuration.ApiBaseUrl ?? ""; Step.Value = SetupStep.Instance; Status.Value = "Abandoned delivery expired and was cleaned up. The working profile was preserved."; });
                        }
                        else
                        {
                            var recoveredAcknowledged = deliveryState?.Status == "consumed" ? _workflow.MarkAcknowledged(recovered) : recovered;
                            await ApplyAsync(generation, token, () =>
                            {
                                _pending = recoveredAcknowledged; _session = recovered.Session; _sessionOrigin = recovered.Origin;
                                _review = recoveredConfiguration; _acknowledged = recoveredAcknowledged.Acknowledged; _deliveryExpires = recovered.DeliveryExpiresAtUtc;
                                ShowClientReview(); Status.Value = "A durable pending candidate was recovered. Review before acknowledgement and activation; the working profile is preserved.";
                            });
                        }
                        break;
                    }
                    var capabilities = await _workflow.DiscoverAsync(origin, token);
                    await ApplyAsync(generation, token, () =>
                    {
                        PairingSupported = capabilities?.PairingSupported == true;
                        if (_options.Authentication is null && !HasExistingIdentity(_profile!.Resolution.Configuration) && !(Transport == SetupTransport.Http && _profile.Resolution.Configuration.HttpClientAuthentication == "oauth") && _options.SeedConfiguration?.AuthentikTokenUrl is null)
                            Authentication = PairingSupported ? "browser" : "manual";
                        Detail.Value = Authentication == "existing" ? "Your existing identity stays bound to this reviewed target. Its current grants and lifetime are preserved."
                            : Authentication == "oauth" ? "The external OAuth client owns login and token storage. Setup retains its reviewed protected-resource metadata."
                            : PairingSupported ? "Continue to browser approval. Open the displayed URL on any signed-in device; no account password is collected here."
                            : "Browser pairing is unavailable. Select an existing identity or an explicit authentication method.";
                        Step.Value = SetupStep.Authentication;
                        Status.Value = "Choose authentication for this client profile.";
                    });
                    break;
                case SetupStep.Authentication:
                    if (Authentication == "oauth" && Transport != SetupTransport.Http)
                        throw new AgentClientValidationException("External OAuth client profiles require HTTP MCP transport. Native CLI and stdio use an API-purpose credential or external Authentik API identity.");
                    if (Authentication == "oauth" || Authentication == "existing" && Transport == SetupTransport.Http && _profile?.Resolution.Configuration.HttpClientAuthentication == "oauth")
                    {
                        if (_profile?.Resolution.Settings.Any(setting => setting.Managed && setting.IsSecret) == true)
                            throw new AgentClientValidationException("External OAuth clients own their tokens separately. Select an isolated profile/environment without managed API or downstream secrets.");
                        var metadata = await _workflow.DiscoverOAuthAsync(Instance.Value, _profile?.Resolution.Configuration.OAuthAuthorizationServers, token);
                        await ApplyAsync(generation, token, () =>
                        {
                            _review = AgentClientConfiguration.Empty with { ApiBaseUrl = Instance.Value.TrimEnd('/'), CredentialTarget = Instance.Value.TrimEnd('/'), ClientTransport = "http",
                                CredentialPurpose = "mcp", McpResourceUri = metadata.Resource, HttpClientAuthentication = "oauth", OAuthAuthorizationServers = metadata.AuthorizationServers,
                                OAuthClientId = string.IsNullOrWhiteSpace(ClientId.Value) ? null : ClientId.Value };
                            ShowClientReview();
                            Status.Value = "External OAuth metadata verified. Login, token storage and actual grants remain owned by your selected MCP client.";
                        });
                        break;
                    }
                    if (Authentication == "browser")
                    {
                        if (_useNativeApiTarget) throw new AgentClientValidationException("Browser approval cannot authorize a different private API destination. Go back and select the public Web URL.");
                        if (!PairingSupported) throw new AgentClientValidationException("This deployment does not advertise browser pairing.");
                        if (Managed("integrationCredential") || Managed("integrationCredentialFile") || Managed("credentialMode"))
                            throw new AgentClientValidationException("Authentication is externally managed. Use the existing managed identity or export a separate profile.");
                        _workflow.PreflightStorage(Profile.Value, Transport);
                        var requestedOrigin = Instance.Value;
                        var session = await _workflow.StartAsync(requestedOrigin, Profile.Value, Transport, token);
                        if (generation != _generation || token.IsCancellationRequested)
                        { await CancelDeliveryAsync(requestedOrigin, session); return; }
                        await ApplyAsync(generation, token, () =>
                        {
                            _session = session; _sessionOrigin = Instance.Value; _acknowledged = false; _saved = false;
                            ApprovalContext = SetupApprovalContext.Client;
                            ApprovalSummary = "The signed-in account reviews the exact client grants; the existing profile is preserved.";
                            DetailsVisible.Value = false;
                            Step.Value = SetupStep.Approval;
                            Detail.Value = $"Trusted approval page: {TerminalText.Sanitize(session.VerificationUri)}\nShort code: {TerminalText.Sanitize(session.HumanCode)}\nOpen this page on any signed-in device. [Ctrl+O] explicitly opens a local browser.";
                            Status.Value = "Approval pending.";
                        });
                        _ = PollApprovalAsync(generation, token, session, Instance.Value);
                    }
                    else
                    {
                        _review = BuildClientConfiguration();
                        ValidateClient(_review);
                        if (Transport == SetupTransport.Http)
                        {
                            var candidate = Authentication == "existing" ? _profile!.Resolution.Configuration with
                            { ClientTransport = "http", CredentialPurpose = "mcp", McpResourceUri = _review.McpResourceUri } : _review;
                            var resolution = _profile!.Resolution with { Configuration = candidate };
                            var authenticationChecks = await _workflow.CheckAsync(resolution, Transport, token, InstanceLabel.Value);
                            if (authenticationChecks.Any(check => check.Status == "failed")) throw new AgentClientValidationException("The existing credential did not complete MCP-purpose initialize and authenticated read-only status checks. No profile was saved.");
                            await ApplyAsync(generation, token, ShowClientReview);
                            break;
                        }
                        ShowClientReview();
                    }
                    break;
                case SetupStep.Hosting:
                    _hosting = BuildHostingSettings();
                    var failures = HttpMcpHostingCoordinator.Validate(_hosting);
                    if (failures.Count > 0) throw new AgentClientValidationException(string.Join(" ", failures));
                    _hostProposal = HttpMcpEffectiveDeployment.Create(_hosting);
                    if (!_hostProposal.Valid) throw new AgentClientValidationException(HostDeploymentWorkflow.Describe(_hostProposal.Diagnostics));
                    await Task.WhenAll(_cleanup);
                    if (token.IsCancellationRequested || generation != _generation) return;
                    _hostWorkflow?.Dispose(); _hostWorkflow = _hostWorkflowFactory(_hosting.PublicOrigin);
                    var hostWorkflow = _hostWorkflow;
                    var hostProposal = _hostProposal;
                    var hostSession = await hostWorkflow.StartAsync(hostProposal, token);
                    if (_closing || token.IsCancellationRequested || generation != _generation)
                    { await CancelHostApprovalAsync(hostWorkflow, hostSession); return; }
                    _hostSession = hostSession;
                    ApprovalContext = SetupApprovalContext.Host;
                    DetailsVisible.Value = false;
                    ApprovalSummary = string.Join("; ", _hostProposal.Changes.Take(2).Select(change => TerminalText.Sanitize(change, 180)));
                    if (ApprovalSummary.Length == 0) ApprovalSummary = "Review the exact server configuration and protected storage changes.";
                    Detail.Value = JsonSerializer.Serialize(HttpMcpHostingCoordinator.Preview(_hosting), AgentClientConfiguration.JsonOptions) +
                        "\n\n" + string.Join('\n', _hostProposal.Changes) + "\nTrusted host review: " + TerminalText.Sanitize(hostSession.VerificationUri) + "\nCode: " + TerminalText.Sanitize(hostSession.HumanCode);
                    Step.Value = SetupStep.Approval;
                    Status.Value = "Host deployment approval pending. An administrator reviews this exact proposal; no gateway credential is created.";
                    _ = PollHostApprovalAsync(generation, token, hostSession, hostWorkflow, hostProposal);
                    break;
                case SetupStep.Review:
                    if (IsHosting)
                    {
                        if (!overwriteConfirmed && File.Exists(System.IO.Path.Combine(OutputDirectory.Value, "host-settings.json")))
                        { ConfirmOverwrite.Value = true; break; }
                        if (!_hostApproved || _hostProposal is null || _hostSession is null || _hostWorkflow is null)
                            throw new AgentClientValidationException("Review and explicitly approve this effective deployment before saving.");
                        var export = await _hostWorkflow.SaveApprovedAsync(_hostProposal, _hostSession, OutputDirectory.Value, overwriteConfirmed, token);
                        _saved = true;
                        Detail.Value = "Private host output: " + TerminalText.Sanitize(OutputDirectory.Value) + "\n" + string.Join('\n', export.Files.Select(path => TerminalText.Sanitize(path))) +
                            "\n\nOn the Docker host, run the same deployment launcher with --apply (release-owned Compose), or import deployment.json through the owning manager.\nAfter recreation, start a fresh invocation using the exact --verify command printed by the host launcher (retains Docker configuration, context, host and TLS selections).\nThis requests a separate temporary status verifier and checks observed runtime, catalog and API-backed status.";
                        Step.Value = SetupStep.Complete;
                        Status.Value = "Saved—apply pending. Deployment-owner apply and end-to-end verification remain required.";
                        break;
                    }
                    if (!_saved && !overwriteConfirmed && _profile?.Resolution.Revision is not null) { ConfirmOverwrite.Value = true; break; }
                    if (!_saved && _pending?.Acknowledged != true && _deliveryExpires is { } deliveryDeadline && deliveryDeadline <= TimeProvider.GetUtcNow())
                        throw new AgentClientValidationException("Protected credential delivery expired before secure save. Go back and obtain fresh browser approval; the existing profile is unchanged.");
                    _review ??= BuildClientConfiguration();
                    ConfigurationProfileSaveResult saved;
                    if (_session is not null && (_credential is not null || _pending is not null))
                    {
                        _pending ??= _workflow.Stage(Profile.Value, Transport, _review, _profile?.Resolution.Revision ?? "", _sessionOrigin!, _session,
                            _credential!.Id, _deliveryExpires ?? _session.ExpiresAtUtc, token);
                        try
                        {
                            if (!_pending.Acknowledged) await _workflow.AcknowledgeAsync(_sessionOrigin!, _session, _pending.CredentialId, token);
                            if (_closing || token.IsCancellationRequested || generation != _generation) return;
                            var acknowledged = _pending.Acknowledged ? _pending : _workflow.MarkAcknowledged(_pending);
                            await ApplyAsync(generation, token, () => { _pending = acknowledged; _acknowledged = true; });
                        }
                        catch (Exception exception) when (exception is HttpRequestException or AgentClientValidationException or OperationCanceledException)
                        {
                            if (_closing || token.IsCancellationRequested || generation != _generation) return;
                            PairingPollResult? delivery = null;
                            try { delivery = await _workflow.PollAsync(_sessionOrigin!, _session, token); }
                            catch (Exception pollFailure) when (pollFailure is HttpRequestException or AgentClientValidationException or OperationCanceledException) { }
                            if (delivery?.Status == "consumed")
                            {
                                var acknowledged = _workflow.MarkAcknowledged(_pending!);
                                await ApplyAsync(generation, token, () => { _pending = acknowledged; _acknowledged = true; });
                            }
                            else if (delivery?.Status is "expired" or "cancelled" or "denied")
                            {
                                _workflow.Discard(_pending!);
                                await ApplyAsync(generation, CancellationToken.None, () => { _pending = null; _session = null; _saved = false; _savedProfile = null; Status.Value = "Protected delivery expired before acknowledgement. The working profile was preserved; obtain fresh browser approval."; Step.Value = SetupStep.Authentication; });
                                return;
                            }
                            if (!_acknowledged)
                            {
                                await ApplyAsync(generation, CancellationToken.None, () =>
                                {
                                    Status.Value = "Pending candidate saved privately, but acknowledgement failed. Retry Next promptly, or cancel; the working profile is unchanged.";
                                    Detail.Value = $"Private pending candidate for: {TerminalText.Sanitize(ProfilePath)}. Setup re-entry can recover delivery; activation waits for confirmed acknowledgement.";
                                });
                                return;
                            }
                        }
                        saved = _workflow.Activate(_pending!, token);
                        _pending = null; _savedProfile = saved; _saved = true;
                    }
                    else { saved = _savedProfile ??= _workflow.Save(Profile.Value, Transport, _review, _profile?.Resolution.Revision ?? "", token); _saved = true; }
                    await ApplyAsync(generation, token, () => { Step.Value = SetupStep.Checks; Status.Value = "Saved securely. Running bounded read-only checks…"; ConfirmOverwrite.Value = false; });
                    var effective = _workflow.Load(Profile.Value, Transport).Resolution;
                    var checks = await _workflow.CheckAsync(effective, Transport, token, InstanceLabel.Value);
                    await ApplyAsync(generation, token, () =>
                    {
                        Detail.Value = string.Join('\n', checks.Select(FormatCheck)) + "\n\n" + LaunchSnippet(saved);
                        Status.Value = checks.Any(check => check.Status == "failed") ? "Saved profile requires correction. Review each check result." : "Client profile saved. Review transport checks and launch instructions.";
                        Step.Value = SetupStep.Complete;
                        ManualSecret.Value = "";
                    });
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is AgentClientValidationException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            await ApplyAsync(generation, CancellationToken.None, () => Status.Value = SafeError(exception));
        }
        finally { if (!_closing && !_disposed && generation == _generation) await InvokeAsync(() => { if (generation == _generation) Busy.Value = false; }); }
    }

    private static async Task DisposeVerifierAsync(IHostVerificationWorkflow verifier, Task? polling = null)
    {
        // The protocol session owns one cleanup journal. Wait for its bounded HTTP poll
        // to observe cancellation before disposing it; UI generation checks alone cannot
        // prevent a late response from racing journal cleanup.
        try { if (polling is not null) await polling; }
        catch (Exception exception) when (exception is OperationCanceledException or AgentClientValidationException or HttpRequestException or IOException) { }
        finally { await verifier.DisposeAsync(); }
    }

    private async Task PollVerificationAsync(long generation, CancellationToken token, PairingSession session, IHostVerificationWorkflow verifier)
    {
        try
        {
            while (!token.IsCancellationRequested && TimeProvider.GetUtcNow() < session.ExpiresAtUtc)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(session.PollIntervalSeconds, 1, 30)), TimeProvider, token);
                var poll = await verifier.PollAsync(token);
                if (token.IsCancellationRequested || generation != _generation) return;
                if (poll.Status is "pending" or "slow_down") continue;
                if (poll.Credential is null)
                {
                    await ApplyAsync(generation, token, () => Status.Value = $"Temporary status approval {TerminalText.Sanitize(poll.Status)}. Back returns to the applied-host verification checkpoint.");
                    return;
                }
                await ApplyAsync(generation, token, () => { Busy.Value = true; Step.Value = SetupStep.Checks; Status.Value = "Applied—verify pending. Running read-only checks and temporary access cleanup…"; });
                var result = await verifier.RunAsync(token);
                await ApplyAsync(generation, token, () =>
                {
                    ExitCode = result.Passed ? 0 : 1;
                    Detail.Value = string.Join('\n', result.Checks.Select(FormatCheck)) + "\n\n" + result.Message;
                    Status.Value = result.Message;
                    Step.Value = SetupStep.Complete;
                    Busy.Value = false;
                    Shutdown();
                });
                return;
            }
            await ApplyAsync(generation, token, () => Status.Value = "Temporary status approval expired. Back returns to applied—verify pending; no host reprovisioning is needed.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is AgentClientValidationException or HttpRequestException or IOException)
        { await ApplyAsync(generation, CancellationToken.None, () => { Busy.Value = false; Status.Value = SafeError(exception); }); }
    }

    private async Task PollHostApprovalAsync(long generation, CancellationToken token, PairingSession session,
        IHostDeploymentWorkflow workflow, HttpMcpDeploymentProposal proposal)
    {
        try
        {
            while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < session.ExpiresAtUtc)
            {
                await Task.Delay(TimeSpan.FromSeconds(session.PollIntervalSeconds), token);
                var poll = await workflow.PollAsync(session, token);
                if (poll.Status == "approved")
                {
                    if (poll.ProposalHash != proposal.ProposalHash || poll.SourceHash != proposal.SourceHash)
                        throw new AgentClientValidationException("The approved host generation differs from the reviewed proposal. Start a new review.");
                    await ApplyAsync(generation, token, () => { _hostApproved = true; Step.Value = SetupStep.Review; Status.Value = "Administrator approved the reviewed generation. Next saves the protected export; apply is still pending."; });
                    return;
                }
                if (poll.Status is "denied" or "expired" or "cancelled")
                {
                    await ApplyAsync(generation, token, () => { Step.Value = SetupStep.Hosting; Status.Value = "Host deployment approval " + poll.Status + ". Inputs are preserved; review before retrying."; });
                    return;
                }
            }
            await ApplyAsync(generation, token, () => { Step.Value = SetupStep.Hosting; Status.Value = "Host deployment review expired. Review again to obtain a fresh code."; });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is AgentClientValidationException or HttpRequestException)
        { await ApplyAsync(generation, token, () => { Step.Value = SetupStep.Hosting; Status.Value = SafeError(exception); }); }
    }

    private static async Task CancelHostApprovalAsync(IHostDeploymentWorkflow workflow, PairingSession session)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await workflow.CancelAsync(session, deadline.Token); }
        catch { /* No credential exists; a pending proposal expires on the server in ten minutes. */ }
    }

    private async Task PollApprovalAsync(long generation, CancellationToken token, PairingSession session, string origin)
    {
        var interval = session.PollIntervalSeconds;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = session.ExpiresAtUtc - TimeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero) { await ApplyAsync(generation, token, () => Status.Value = "Approval expired. Go back to start a new request."); return; }
                await ApplyAsync(generation, token, () => Status.Value = $"Approval pending · expires in {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2} · [Ctrl+O] Open browser");
                await Task.Delay(TimeSpan.FromSeconds(interval), TimeProvider, token);
                var result = await _workflow.PollAsync(origin, session, token);
                interval = Math.Max(interval, result.PollIntervalSeconds);
                if (result.Status is "pending" or "slow_down") continue;
                await ApplyAsync(generation, token, () =>
                {
                    if (result.Status == "issued" && result.Credential is { } credential)
                    {
                        var purpose = Transport == SetupTransport.Http ? "mcp" : "api";
                        var expectedResource = Transport == SetupTransport.Http ? new Uri(IntegrationCredentialPairingClient.ValidateOrigin(origin), "/mcp").AbsoluteUri : null;
                        if (credential.GrantKind != "ordinary" || credential.Purpose != purpose || credential.McpResourceUri != expectedResource || credential.ExpiresAtUtc <= TimeProvider.GetUtcNow())
                            throw new AgentClientValidationException("Approved credential purpose/resource does not match this reviewed client.");
                        _credential = credential;
                        _deliveryExpires = result.DeliveryExpiresAtUtc ?? session.ExpiresAtUtc;
                        _review = BuildClientConfiguration();
                        ShowClientReview();
                        Status.Value = "Browser approved the displayed organization, permissions and expiry. Review before saving.";
                        _ = WatchDeliveryAsync(generation, token);
                    }
                    else Status.Value = $"Approval {result.Status}. Go back or cancel; the existing profile is unchanged.";
                });
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is AgentClientValidationException or HttpRequestException or IOException)
        { await ApplyAsync(generation, CancellationToken.None, () => Status.Value = SafeError(exception)); }
    }

    private Task ApplyAsync(long generation, CancellationToken ct, Action action) => _disposed || _closing || generation != _generation
        ? Task.CompletedTask : InvokeAsync(() => { if (!_disposed && !_closing && _generation == generation && !ct.IsCancellationRequested) action(); }, ct);
    private async Task WatchDeliveryAsync(long generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_acknowledged && !_saved && _deliveryExpires is { } deadline)
            {
                var remaining = deadline - TimeProvider.GetUtcNow();
                await ApplyAsync(generation, token, () => Status.Value = remaining > TimeSpan.Zero
                    ? $"Browser approved the displayed grants. Save and acknowledge within {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}."
                    : "Protected delivery expired. Go back for fresh approval; the existing profile remains unchanged.");
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider, token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private static string SafeError(Exception exception) => exception is HttpRequestException
        ? "The trusted instance could not be reached. Check the URL, TLS certificate and connection; your inputs are preserved."
        : TerminalText.Sanitize(exception.Message);

    private static string FormatCheck(SetupCheck check)
    {
        var context = new[] { check.Code, check.Layer, check.UpstreamStatus is { } status ? $"HTTP {status}" : null,
            check.CorrelationId is { } correlation ? $"reference {correlation}" : null }.Where(value => !string.IsNullOrWhiteSpace(value));
        var safeContext = string.Join(" · ", context.Select(value => TerminalText.Sanitize(value)));
        return $"{TerminalText.Sanitize(check.Name)}: {TerminalText.Sanitize(check.Status)} — {TerminalText.Sanitize(check.Message)}" +
            (safeContext.Length == 0 ? "" : $" [{safeContext}]") +
            (check.NextAction is null ? "" : "\nNext: " + TerminalText.Sanitize(check.NextAction));
    }

    private string ClientTarget => (_useNativeApiTarget
        ? AgentClientConfigurationValidator.ApiUri(NativeApiTarget.Value).AbsoluteUri
        : IntegrationCredentialPairingClient.ValidateOrigin(Instance.Value).AbsoluteUri).TrimEnd('/');

    private AgentClientConfiguration BuildClientConfiguration()
    {
        var stored = _profile?.Stored ?? AgentClientConfiguration.Empty;
        var effective = _profile?.Resolution.Configuration ?? stored;
        var origin = ClientTarget;
        if (Authentication == "existing")
        {
            if (!AgentClientConfigurationValidator.SameTargetOrEmpty(effective.ApiBaseUrl, origin))
                throw new AgentClientValidationException("An existing credential cannot be sent to a newly edited origin. Reauthorize or import a different credential.");
            if (stored.IntegrationCredential is null && stored.IntegrationCredentialFile is null && stored.AuthentikAppPassword is null && stored.AuthentikAppPasswordFile is null &&
                !Managed("integrationCredential") && !Managed("integrationCredentialFile") && !Managed("authentikAppPassword") && !Managed("authentikAppPasswordFile"))
                throw new AgentClientValidationException("No existing or externally managed credential is configured.");
            if (Transport == SetupTransport.Http && (effective.CredentialPurpose == "api" ||
                effective.McpResourceUri is { } resource && resource != origin + "/mcp"))
                throw new AgentClientValidationException("Select an MCP-purpose credential for this exact /mcp resource. An API-purpose profile cannot be reused as an HTTP MCP client.");
            if (Transport != SetupTransport.Http && effective.CredentialPurpose == "mcp")
                throw new AgentClientValidationException("Native CLI and stdio require an API-purpose credential. An MCP-purpose HTTP credential cannot be reused for API access.");
            return stored with { ApiBaseUrl = Managed("apiBaseUrl") ? stored.ApiBaseUrl ?? origin : origin, ClientTransport = Transport.ToString().ToLowerInvariant(),
                CredentialPurpose = Transport == SetupTransport.Http ? "mcp" : stored.CredentialPurpose,
                McpResourceUri = Transport == SetupTransport.Http ? origin + "/mcp" : stored.McpResourceUri };
        }
        if (Managed("credentialMode") || Managed("integrationCredential") || Managed("integrationCredentialFile") || Managed("authentikAppPassword") || Managed("authentikAppPasswordFile"))
            throw new AgentClientValidationException("Authentication is externally managed. Use existing managed authentication or a separate profile/environment.");
        var config = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = Managed("apiBaseUrl") ? stored.ApiBaseUrl ?? origin : origin,
            CredentialTarget = origin,
            ClientTransport = Transport.ToString().ToLowerInvariant(),
            CredentialMode = Authentication == "authentik" ? "authentik" : "integration",
            CredentialPurpose = Transport == SetupTransport.Http ? "mcp" : "api",
            HttpClientAuthentication = Transport == SetupTransport.Http ? "integration" : null,
            McpResourceUri = Transport == SetupTransport.Http ? origin + "/mcp" : null,
            AgentUserEmail = _options.SeedConfiguration?.AgentUserEmail ?? stored.AgentUserEmail
        };
        if (Authentication == "authentik")
        {
            if (Transport == SetupTransport.Http) throw new AgentClientValidationException("HTTP MCP external Authentik clients use their configured OAuth client. Use deployment settings for Authentik ingress; this profile imports a MCP-purpose credential.");
            return config with { AuthentikTokenUrl = TokenUrl.Value, AuthentikClientId = ClientId.Value, AuthentikUsername = Username.Value,
                AuthentikScope = Scope.Value, AuthentikAppPassword = string.IsNullOrWhiteSpace(SecretFile.Value) ? ManualSecret.Value : null,
                AuthentikAppPasswordFile = string.IsNullOrWhiteSpace(SecretFile.Value) ? null : SecretFile.Value };
        }
        if (_credential is { } approved)
            return config with { IntegrationCredential = approved.Secret, CredentialOrganizationId = approved.OrganizationId,
                CredentialPermissions = approved.Permissions, CredentialExpiresAtUtc = approved.ExpiresAtUtc };
        return config with { IntegrationCredential = string.IsNullOrWhiteSpace(SecretFile.Value) ? ManualSecret.Value : null,
            IntegrationCredentialFile = string.IsNullOrWhiteSpace(SecretFile.Value) ? null : SecretFile.Value };
    }

    private void ValidateClient(AgentClientConfiguration configuration)
    {
        // Existing managed values are evaluated by the same engine, never copied into a file.
        var effective = Authentication == "existing" ? _profile!.Resolution.Configuration : configuration;
        if (Transport != SetupTransport.Http) _ = effective.Resolve();
        else if (string.IsNullOrWhiteSpace(effective.IntegrationCredential) && string.IsNullOrWhiteSpace(effective.IntegrationCredentialFile))
            throw new AgentClientValidationException("Import the existing MCP-purpose credential or select browser approval.");
    }

    private void ShowClientReview()
    {
        _review ??= BuildClientConfiguration();
        var grant = _review.HttpClientAuthentication == "oauth" ? "OAuth login and actual organization/permission grants are selected by the external MCP client; setup has not verified them."
            : _pending is not null ? $"Recovered approved organization: {TerminalText.Sanitize(_review.CredentialOrganizationId)}\nPermissions: {string.Join(", ", (_review.CredentialPermissions ?? []).Select(value => TerminalText.Sanitize(value)))}\nExpires: {_review.CredentialExpiresAtUtc:O}"
            : _credential is { } approved
            ? $"Approved organization: {TerminalText.Sanitize(approved.OrganizationId)}\nPermissions: {string.Join(", ", approved.Permissions.Select(value => TerminalText.Sanitize(value)))}\nExpires: {approved.ExpiresAtUtc:O}"
            : "Manual/existing credential grants are determined by the server; no permissions are inferred.";
        var storage = _review.HttpClientAuthentication == "oauth" ? "Non-secret profile metadata; OAuth tokens remain in the external MCP client's storage." : "Private file reference; secrets remain masked.";
        Detail.Value = $"Profile: {TerminalText.Sanitize(Profile.Value)}\nTransport: {Transport.ToString().ToLowerInvariant()}\nTarget: {TerminalText.Sanitize(ClientTarget)}\nPurpose: {(Transport == SetupTransport.Http ? "mcp" : "api")}\nPrivate profile: {TerminalText.Sanitize(ProfilePath)}\nStorage: {storage}\n{(_profile?.Resolution.Settings.Any(setting => setting.Managed) == true ? "Externally managed sources remain required; their secrets are not copied into this profile.\n" : "")}{grant}\n\n{JsonSerializer.Serialize(AgentClientConfigurationResolver.Redact(_review), AgentClientConfiguration.JsonOptions)}";
        Step.Value = SetupStep.Review;
        Status.Value = "Review the target, actual grants and storage destination. Next saves atomically.";
    }

    private HttpMcpHostingSettings BuildHostingSettings() => new()
    {
        PublicOrigin = HostPublicOrigin.Value, ApiBaseUrl = ApiBackend.Value, McpDestination = McpBackend.Value,
        Instance = InstanceLabel.Value, AuthenticationMode = HostAuthentication,
        DelegationTimeoutSeconds = _hosting.DelegationTimeoutSeconds,
        AllowedOrigins = Split(AllowedOrigins.Value.Length == 0 ? HostPublicOrigin.Value : AllowedOrigins.Value), Listener = Listener.Value,
        AuthentikAuthority = HostAuthentication == "authentik" ? Authority.Value : null,
        AuthentikAudience = HostAuthentication == "authentik" ? (Audience.Value.Length == 0 ? HostPublicOrigin.Value.TrimEnd('/') + "/mcp" : Audience.Value) : null,
        RequiredScopes = HostAuthentication == "authentik" ? Split(RequiredScopes.Value) : [],
        RequiredGroups = HostAuthentication == "authentik" ? Split(RequiredGroups.Value) : [],
        Outbound = BuildDownstreamIdentity()
    };
    private AgentClientConfiguration BuildDownstreamIdentity()
    {
        if (HostAuthentication == "gateway")
        {
            if (HostManaged("Outbound") && _hosting.Outbound is { CredentialMode: not "gateway" })
                throw new AgentClientValidationException("The externally managed downstream identity conflicts with gateway mode. Change its owning source before applying a gateway export.");
            return AgentClientConfiguration.Empty with { ApiBaseUrl = ApiBackend.Value, CredentialMode = "gateway" };
        }
        if (DownstreamAuthentication == "existing")
        {
            if (_hosting.Outbound is not { } existing) throw new AgentClientValidationException("Load an existing deployment export before retaining its downstream identity.");
            if (!AgentClientConfigurationValidator.SameTargetOrEmpty(existing.ApiBaseUrl, ApiBackend.Value))
                throw new AgentClientValidationException("The existing downstream identity is bound to another API backend. Explicitly configure a reauthorized replacement identity.");
            return existing;
        }
        if (DownstreamAuthentication == "integration") return AgentClientConfiguration.Empty with
        { ApiBaseUrl = ApiBackend.Value, CredentialMode = "integration", IntegrationCredentialFile = SecretFile.Value, CredentialTarget = ApiBackend.Value, CredentialPurpose = "api" };
        return new(ApiBackend.Value, TokenUrl.Value, ClientId.Value, Username.Value, null, Scope.Value, null)
        { CredentialMode = "authentik", AuthentikAppPasswordFile = SecretFile.Value, CredentialTarget = ApiBackend.Value };
    }
    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void LoadHosting()
    {
        var inspection = HttpMcpHostingCoordinator.Inspect(OutputDirectory.Value);
        _hosting = HttpMcpEffectiveDeployment.Adopt(inspection.Settings); _hostManaged = inspection.ManagedFields;
        HostPublicOrigin.Value = _hosting.PublicOrigin; ApiBackend.Value = _hosting.ApiBaseUrl; McpBackend.Value = _hosting.McpDestination;
        InstanceLabel.Value = _hosting.Instance; HostAuthentication = _hosting.AuthenticationMode;
        AllowedOrigins.Value = string.Join(',', _hosting.AllowedOrigins); Listener.Value = _hosting.Listener;
        Authority.Value = _hosting.AuthentikAuthority ?? ""; Audience.Value = _hosting.AuthentikAudience ?? "";
        RequiredScopes.Value = string.Join(',', _hosting.RequiredScopes); RequiredGroups.Value = string.Join(',', _hosting.RequiredGroups);
        TokenUrl.Value = _hosting.Outbound?.AuthentikTokenUrl ?? ""; ClientId.Value = _hosting.Outbound?.AuthentikClientId ?? "";
        Username.Value = _hosting.Outbound?.AuthentikUsername ?? ""; Scope.Value = _hosting.Outbound?.AuthentikScope ?? AgentClientConfiguration.DefaultScope;
        SecretFile.Value = _hosting.Outbound?.AuthentikAppPasswordFile ?? "";
        DownstreamAuthentication = _hosting.Outbound is null ? "authentik" : "existing";
        Status.Value = "Existing deployment settings inspected. Advanced ingress/downstream fields are behind F2.";
    }

    private string LaunchSnippet(ConfigurationProfileSaveResult saved)
    {
        var path = ShellQuote(saved.Path);
        var management = _useNativeApiTarget ? "Open your RatelDesk Web account integration credentials page."
            : IntegrationCredentialPairingClient.ValidateOrigin(Instance.Value).AbsoluteUri.TrimEnd('/') + "/account/integration-credentials";
        return Transport switch
        {
            SetupTransport.Cli => $"Private storage: {TerminalText.Sanitize(saved.Path)}\nLaunch: rateldesk --config {path} auth status\nVerify again: rateldesk --config {path} doctor --network --authenticate\nCredential management: {management}",
            SetupTransport.Stdio => $"Private storage: {TerminalText.Sanitize(saved.Path)}\nVerify again: rateldesk-mcp --config {path} doctor --network --authenticate\nLaunch with pipes (no TTY):\nRATELDESK_MCP_CONFIG={path} RATELDESK_MCP_INSTANCE={ShellQuote(InstanceLabel.Value)} RATELDESK_MCP_EXPECTED_API_BASE_URL={ShellQuote(ClientTarget)} rateldesk-mcp\nCredential management: {management}",
            _ when saved.Configuration.HttpClientAuthentication == "oauth" =>
                $"OAuth-capable MCP client settings:\n{JsonSerializer.Serialize(new { url = saved.Configuration.McpResourceUri, authorizationServers = saved.Configuration.OAuthAuthorizationServers, clientId = saved.Configuration.OAuthClientId }, AgentClientConfiguration.JsonOptions)}\nUse these reviewed non-secret values in your client's supported OAuth configuration. It owns login, grants and token storage; API app passwords and rdk credentials are not reused.\nAuthentication / initialize / tool-result: client-managed, unverified until the external client connects.",
            _ => $"Private storage: {TerminalText.Sanitize(saved.Path)}\nVerify again: dotnet Helpdesk.Mcp.Http.dll --config {path} doctor --authenticate\nMCP URL: {Instance.Value.TrimEnd('/')}/mcp\nBearer reference: {TerminalText.Sanitize(saved.Configuration.IntegrationCredentialFile)}\nConfigure your MCP client to read that private file; never paste its secret into shell history.\nCredential management: {management}"
        };
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public void Back()
    {
        if (_closing || (Busy.Value && Step.Value is not (SetupStep.Approval or SetupStep.Verification)) || Step.Value == SetupStep.Complete) return;
        if (_saved && !_acknowledged) { Status.Value = "Secure save completed; retry Next to acknowledge delivery before changing this profile."; return; }
        var previous = Step.Value;
        var context = ApprovalContext;
        InvalidateTarget(preserveDraft: true);
        Step.Value = previous switch
        {
            SetupStep.Profile or SetupStep.Hosting => SetupStep.Instance,
            SetupStep.Instance => SetupStep.Instance,
            SetupStep.Authentication => SetupStep.Instance,
            SetupStep.Approval when context == SetupApprovalContext.Host => SetupStep.Hosting,
            SetupStep.Approval when context == SetupApprovalContext.Verification => SetupStep.Verification,
            SetupStep.Approval => SetupStep.Authentication,
            SetupStep.Review => IsHosting ? SetupStep.Hosting : SetupStep.Authentication,
            _ => previous
        };
        _review = null;
        Status.Value = context == SetupApprovalContext.Verification && previous == SetupStep.Approval
            ? "Applied—verify pending. Temporary consent cancelled; Next retries verification of the same applied host generation."
            : previous == SetupStep.Approval && context == SetupApprovalContext.Host
                ? "Host draft retained. Review your settings before requesting fresh deployment approval."
                : "Inputs are preserved. Review any changed target before authentication.";
    }
    public async Task OpenBrowserAsync()
    {
        if (BrowserUrl is not { } url) return;
        var generation = _generation;
        try { await _workflow.OpenBrowserAsync(url, _operation.Token); await ApplyAsync(generation, CancellationToken.None, () => Status.Value = "Browser opened. The same trusted link and code also work on another device."); }
        catch { await ApplyAsync(generation, CancellationToken.None, () => Status.Value = "Browser launch unavailable. Open the displayed trusted URL on another device and enter the short code."); }
    }
    public async Task CancelAsync()
    {
        if (_closing) return;
        if (Step.Value == SetupStep.Complete) { _closing = true; Shutdown(); return; }
        if (!IsHosting && _pending is null && _profile?.Pending is { Acknowledged: false } recovered)
        {
            _pending = recovered; _session = recovered.Session; _sessionOrigin = recovered.Origin;
            _profile = _profile with { Pending = null, PendingConfiguration = null };
        }
        if (_saved && !_acknowledged && _session is not null)
        {
            try
            {
                _workflow.Rollback(_savedProfile!, _profile!);
                _saved = false; _savedProfile = null;
            }
            catch (AgentClientValidationException exception)
            {
                Status.Value = TerminalText.Sanitize(exception.Message) + " The later writer was preserved; pairing delivery will expire if unacknowledged.";
                _closing = true; Shutdown(); return;
            }
        }
        if (_options.VerificationDirectory is not null) ExitCode = 130;
        _closing = true;
        Status.Value = "Cancelling approval and stopping setup…";
        InvalidateTarget();
        await Task.WhenAll(_cleanup);
        await InvokeAsync(() =>
        {
            Status.Value = _options.VerificationDirectory is null ? "Cancelled. Existing profiles are unchanged."
                : "Verification cancelled. Applied host settings remain verify pending; rerun the exact --verify command printed by the host launcher.";
            Shutdown();
        });
    }
    public async Task CleanupAsync()
    {
        if (!_saved && _hostSession is { } hostSession && _hostWorkflow is { } hostWorkflow)
        { _hostSession = null; _cleanup.Add(CancelHostApprovalAsync(hostWorkflow, hostSession)); }
        ++_generation;
        _operation.Cancel();
        if (!_saved && !_acknowledged && _session is { } session && _sessionOrigin is { } origin)
        { _session = null; _cleanup.Add(CancelDeliveryAsync(origin, session)); }
        if (_verifier is { } verifier)
        { _verifier = null; var polling = _verificationPolling; _verificationPolling = null; _cleanup.Add(DisposeVerifierAsync(verifier, polling)); }
        await Task.WhenAll(_cleanup);
        if (!_saved && !_acknowledged && _pending is { } pending)
        { _workflow.Discard(pending); _pending = null; }
        _hostWorkflow?.Dispose(); _hostWorkflow = null;
    }
    public override void OnDeactivating()
    {
        _operation.Cancel();
        if (!_saved && !_acknowledged && _session is { } session && _sessionOrigin is { } origin)
        { _session = null; _cleanup.Add(CancelDeliveryAsync(origin, session)); }
        base.OnDeactivating();
    }
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing = true;
        _operation.Cancel(); _operation.Dispose();
        foreach (var property in new IDisposable[] { Step, Status, Detail, Busy, ConfirmOverwrite, Advanced, TerminalWidth, TerminalHeight, DetailsVisible, Profile, Instance, NativeApiTarget, ManualSecret, TokenUrl, ClientId, Username, Scope, SecretFile, InstanceLabel, HostPublicOrigin, ApiBackend, McpBackend, AllowedOrigins, Listener, Authority, Audience, RequiredScopes, RequiredGroups, OutputDirectory }) property.Dispose();
        base.Dispose();
    }
}
