using System.Text.Json;
using Helpdesk.AgentClient;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Helpdesk.Setup;

public sealed class SetupViewModel : ReactiveViewModel
{
    private readonly ISetupWorkflow _workflow;
    private CancellationTokenSource _operation = new();
    private long _generation;
    private PairingSession? _session;
    private string? _sessionOrigin;
    private PairedIntegrationCredential? _credential;
    private SetupProfile? _profile;
    private AgentClientConfiguration? _review;
    private bool _acknowledged;
    private bool _saved;
    private ConfigurationProfileSaveResult? _savedProfile;
    private PendingPairingProfile? _pending;
    private DateTimeOffset? _deliveryExpires;
    private bool _disposed;
    private bool _closing;
    private readonly List<Task> _cleanup = [];
    private HttpMcpHostingSettings _hosting = new();
    private IReadOnlyDictionary<string, string> _hostManaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SetupViewModel(ISetupWorkflow workflow, SetupOptions options)
    {
        _workflow = workflow;
        Transport = options.Transport;
        IsHosting = options.Hosting;
        Profile.Value = options.Profile;
        OutputDirectory.Value = options.OutputDirectory ?? System.IO.Path.Combine(Environment.CurrentDirectory, "rateldesk-hosting");
        Step.Value = options.Hosting ? SetupStep.Hosting : SetupStep.Purpose;
        Instance.Subscribe(_ => InvalidateTarget()).DisposeWith(Subscriptions);
        Profile.Subscribe(_ => InvalidateTarget()).DisposeWith(Subscriptions);
        if (options.Hosting) LoadHosting();
    }

    public ReactiveProperty<SetupStep> Step { get; } = new(SetupStep.Purpose);
    public ReactiveProperty<string> Status { get; } = new("Choose a setup task. All checks are read-only.");
    public ReactiveProperty<string> Detail { get; } = new("");
    public ReactiveProperty<bool> Busy { get; } = new(false);
    public ReactiveProperty<bool> ConfirmOverwrite { get; } = new(false);
    public ReactiveProperty<bool> Advanced { get; } = new(false);
    public ReactiveProperty<int> TerminalWidth { get; } = new(80);
    public ReactiveProperty<string> Profile { get; } = new("default");
    public ReactiveProperty<string> Instance { get; } = new("");
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
    public string? BrowserUrl => _session?.VerificationUri;
    public string? HumanCode => _session?.HumanCode;
    public string? ProfilePath => _profile?.Resolution.ProfilePath;
    public string Footer => Step.Value == SetupStep.Complete ? "[Enter/Esc] Exit" : "[Ctrl+B] Back   [Ctrl+N/Enter] Next   [Esc/Ctrl+C] Cancel   [Tab/Shift+Tab] Focus   [F2] Advanced";
    public bool Managed(string key) => _profile?.Resolution.Settings.Any(setting => setting.Managed && setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true;
    public string ManagedSource(string key) => _profile?.Resolution.Settings.FirstOrDefault(setting => setting.Managed && setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Source ?? "external configuration";
    public bool HostManaged(string key) => _hostManaged.ContainsKey(key);
    public string HostManagedSource(string key) => _hostManaged.TryGetValue(key, out var source) ? source : "external configuration";

    public override void OnActivated()
    {
        Input.OfType<IInputEvent, ResizeEvent>().Subscribe(resize => TerminalWidth.Value = resize.Width).DisposeWith(Subscriptions);
    }

    public void SelectPurpose(bool hosting) { IsHosting = hosting; Status.Value = hosting ? "Export reviewed local HTTP MCP deployment settings." : "Connect a named client profile."; }
    public void SelectTransport(SetupTransport transport) { if (Transport != transport) { InvalidateTarget(); Transport = transport; } Status.Value = $"Selected {transport.ToString().ToLowerInvariant()} client."; }
    public void SelectAuthentication(string authentication) { Authentication = authentication; Status.Value = authentication == "browser" ? "The signed-in browser selects the actual grants." : "Use an existing credential or explicit external identity."; }
    public void SelectHostAuthentication(string mode) { HostAuthentication = mode; Status.Value = mode == "gateway" ? "Gateway mode has no stored downstream deployment credential." : "Authentik ingress and downstream API identity are separate configurations."; }
    public void SelectDownstreamAuthentication(string mode) { DownstreamAuthentication = mode; Status.Value = "Downstream identity is a separate deliberate choice. Existing identities stay bound to their reviewed API backend."; }
    public void ToggleAdvanced() { Advanced.Value = !Advanced.Value; }

    private void InvalidateTarget()
    {
        ++_generation;
        _operation.Cancel();
        _operation.Dispose();
        _operation = new();
        _credential = null;
        _review = null;
        _deliveryExpires = null;
        if (Step.Value is SetupStep.Instance or SetupStep.Authentication or SetupStep.Approval)
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
                case SetupStep.Purpose:
                    if (IsHosting) LoadHosting();
                    Step.Value = IsHosting ? SetupStep.Hosting : SetupStep.Profile;
                    break;
                case SetupStep.Profile:
                    _profile = _workflow.Load(Profile.Value, Transport);
                    var config = _profile.Resolution.Configuration;
                    Instance.Value = _profile.Resolution.Settings.Any(s => s.Key == "apiBaseUrl" && s.Source != "default") ? config.ApiBaseUrl ?? "" : "";
                    TokenUrl.Value = config.AuthentikTokenUrl ?? "";
                    ClientId.Value = config.OAuthClientId ?? config.AuthentikClientId ?? "";
                    Username.Value = config.AuthentikUsername ?? "";
                    Scope.Value = config.AuthentikScope ?? AgentClientConfiguration.DefaultScope;
                    SecretFile.Value = config.IntegrationCredentialFile ?? config.AuthentikAppPasswordFile ?? "";
                    generation = _generation;
                    if (_profile.Pending is { } recovered)
                    {
                        Instance.Value = recovered.Origin;
                        generation = _generation; token = _operation.Token;
                        var recoveredConfiguration = _profile.PendingConfiguration;
                        var deliveryState = recovered.Acknowledged ? null : await _workflow.PollAsync(recovered.Origin, recovered.Session, token);
                        if (_closing || token.IsCancellationRequested || generation != _generation) return;
                        if (deliveryState?.Status is "expired" or "cancelled" or "denied")
                        {
                            _workflow.Discard(recovered);
                            await ApplyAsync(generation, token, () => { _pending = null; _session = null; _review = null; Instance.Value = config.ApiBaseUrl ?? ""; Step.Value = SetupStep.Instance; Status.Value = "Abandoned delivery expired and was cleaned up. The working profile was preserved."; });
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
                    Step.Value = SetupStep.Instance;
                    Status.Value = "Enter the trusted instance HTTPS origin. Existing credentials are withheld from discovery.";
                    break;
                case SetupStep.Instance:
                    var origin = IntegrationCredentialPairingClient.ValidateOrigin(Instance.Value).AbsoluteUri;
                    if (Managed("apiBaseUrl") && !AgentClientConfigurationValidator.SameTarget(new Uri(_profile!.Resolution.Configuration.ApiBaseUrl!), new Uri(origin)))
                        throw new AgentClientValidationException("The instance URL is externally managed. Change its owning environment, or use a separate environment/profile.");
                    var capabilities = await _workflow.DiscoverAsync(origin, token);
                    await ApplyAsync(generation, token, () =>
                    {
                        PairingSupported = capabilities?.PairingSupported == true;
                        Authentication = PairingSupported ? "browser" : "manual";
                        Detail.Value = PairingSupported ? "Browser approval is supported. No terminal login password is needed." : "Browser pairing is unavailable on this deployment. Use an existing credential or explicit external-auth configuration.";
                        Step.Value = SetupStep.Authentication;
                        Status.Value = "Choose authentication for this client profile.";
                    });
                    break;
                case SetupStep.Authentication:
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
                        if (!PairingSupported) throw new AgentClientValidationException("This deployment does not advertise browser pairing.");
                        if (Managed("integrationCredential") || Managed("integrationCredentialFile") || Managed("credentialMode"))
                            throw new AgentClientValidationException("Authentication is externally managed. Use the existing managed identity or export a separate profile.");
                        var requestedOrigin = Instance.Value;
                        var session = await _workflow.StartAsync(requestedOrigin, Profile.Value, Transport, token);
                        if (generation != _generation || token.IsCancellationRequested)
                        { await CancelDeliveryAsync(requestedOrigin, session); return; }
                        await ApplyAsync(generation, token, () =>
                        {
                            _session = session; _sessionOrigin = Instance.Value; _acknowledged = false; _saved = false;
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
                    Detail.Value = JsonSerializer.Serialize(HttpMcpHostingCoordinator.Preview(_hosting), AgentClientConfiguration.JsonOptions);
                    Step.Value = SetupStep.Review;
                    Status.Value = "Review the redacted deployment export. Local export does not grant server administration.";
                    break;
                case SetupStep.Review:
                    if (IsHosting)
                    {
                        if (!overwriteConfirmed && File.Exists(System.IO.Path.Combine(OutputDirectory.Value, "host-settings.json")))
                        { ConfirmOverwrite.Value = true; break; }
                        var export = HttpMcpHostingCoordinator.Export(_hosting, OutputDirectory.Value, overwriteConfirmed);
                        Detail.Value = string.Join('\n', export.Files) + "\n\n" + export.ApplyInstructions;
                        Step.Value = SetupStep.Complete;
                        Status.Value = "Hosting export saved. Apply/recreate steps are explicit; running containers have not been changed.";
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
                        Detail.Value = string.Join('\n', checks.Select(check => $"{check.Name}: {check.Status} — {TerminalText.Sanitize(check.Message)}")) + "\n\n" + LaunchSnippet(saved);
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
        finally { if (!_closing && !_disposed) await InvokeAsync(() => Busy.Value = false); }
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
                        if (credential.Purpose != purpose || credential.McpResourceUri != expectedResource || credential.ExpiresAtUtc <= TimeProvider.GetUtcNow())
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

    private AgentClientConfiguration BuildClientConfiguration()
    {
        var stored = _profile?.Stored ?? AgentClientConfiguration.Empty;
        var effective = _profile?.Resolution.Configuration ?? stored;
        var origin = IntegrationCredentialPairingClient.ValidateOrigin(Instance.Value).AbsoluteUri.TrimEnd('/');
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
            AgentUserEmail = stored.AgentUserEmail
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
        Detail.Value = $"Profile: {TerminalText.Sanitize(Profile.Value)}\nTransport: {Transport.ToString().ToLowerInvariant()}\nTarget: {TerminalText.Sanitize(Instance.Value)}\nPurpose: {(Transport == SetupTransport.Http ? "mcp" : "api")}\nPrivate profile: {TerminalText.Sanitize(ProfilePath)}\nStorage: {storage}\n{(_profile?.Resolution.Settings.Any(setting => setting.Managed) == true ? "Externally managed sources remain required; their secrets are not copied into this profile.\n" : "")}{grant}\n\n{JsonSerializer.Serialize(AgentClientConfigurationResolver.Redact(_review), AgentClientConfiguration.JsonOptions)}";
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
        _hosting = inspection.Settings; _hostManaged = inspection.ManagedFields;
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
        var management = IntegrationCredentialPairingClient.ValidateOrigin(Instance.Value).AbsoluteUri.TrimEnd('/') + "/account/integration-credentials";
        return Transport switch
        {
            SetupTransport.Cli => $"Launch: rateldesk --config {path} auth status\nCredential management: {management}",
            SetupTransport.Stdio => $"Launch with pipes (no TTY):\nRATELDESK_MCP_CONFIG={path} RATELDESK_MCP_INSTANCE={ShellQuote(InstanceLabel.Value)} RATELDESK_MCP_EXPECTED_API_BASE_URL={ShellQuote(Instance.Value)} rateldesk-mcp\nCredential management: {management}",
            _ when saved.Configuration.HttpClientAuthentication == "oauth" =>
                $"OAuth-capable MCP client settings:\n{JsonSerializer.Serialize(new { url = saved.Configuration.McpResourceUri, authorizationServers = saved.Configuration.OAuthAuthorizationServers, clientId = saved.Configuration.OAuthClientId }, AgentClientConfiguration.JsonOptions)}\nUse these reviewed non-secret values in your client's supported OAuth configuration. It owns login, grants and token storage; API app passwords and rdk credentials are not reused.\nAuthentication / initialize / tool-result: client-managed, unverified until the external client connects.",
            _ => $"MCP URL: {Instance.Value.TrimEnd('/')}/mcp\nBearer reference: {TerminalText.Sanitize(saved.Configuration.IntegrationCredentialFile)}\nConfigure your MCP client to read that private file; never paste its secret into shell history.\nCredential management: {management}"
        };
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public void Back()
    {
        if (_closing || Busy.Value || Step.Value == SetupStep.Complete) return;
        if (_saved && !_acknowledged) { Status.Value = "Secure save completed; retry Next to acknowledge delivery before changing this profile."; return; }
        InvalidateTarget();
        Step.Value = Step.Value switch
        {
            SetupStep.Profile or SetupStep.Hosting => SetupStep.Purpose,
            SetupStep.Instance => SetupStep.Profile,
            SetupStep.Authentication => SetupStep.Instance,
            SetupStep.Approval => SetupStep.Authentication,
            SetupStep.Review => IsHosting ? SetupStep.Hosting : SetupStep.Authentication,
            _ => Step.Value
        };
        _review = null;
        Status.Value = "Inputs are preserved. Review any changed target before authentication.";
    }
    public async Task OpenBrowserAsync()
    {
        if (_session?.VerificationUri is not { } url) return;
        var generation = _generation;
        try { await _workflow.OpenBrowserAsync(url, _operation.Token); await ApplyAsync(generation, CancellationToken.None, () => Status.Value = "Browser opened. The same trusted link and code also work on another device."); }
        catch { await ApplyAsync(generation, CancellationToken.None, () => Status.Value = "Browser launch unavailable. Open the displayed trusted URL on another device and enter the short code."); }
    }
    public async Task CancelAsync()
    {
        if (_closing) return;
        if (Step.Value == SetupStep.Complete) { _closing = true; Shutdown(); return; }
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
        _closing = true;
        Status.Value = "Cancelling approval and stopping setup…";
        InvalidateTarget();
        await Task.WhenAll(_cleanup);
        await InvokeAsync(() => { Status.Value = "Cancelled. Existing profiles are unchanged."; Shutdown(); });
    }
    public async Task CleanupAsync()
    {
        ++_generation;
        _operation.Cancel();
        if (!_saved && !_acknowledged && _session is { } session && _sessionOrigin is { } origin)
        { _session = null; _cleanup.Add(CancelDeliveryAsync(origin, session)); }
        await Task.WhenAll(_cleanup);
        if (!_saved && !_acknowledged && _pending is { } pending)
        { _workflow.Discard(pending); _pending = null; }
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
        foreach (var property in new IDisposable[] { Step, Status, Detail, Busy, ConfirmOverwrite, Advanced, TerminalWidth, Profile, Instance, ManualSecret, TokenUrl, ClientId, Username, Scope, SecretFile, InstanceLabel, HostPublicOrigin, ApiBackend, McpBackend, AllowedOrigins, Listener, Authority, Audience, RequiredScopes, RequiredGroups, OutputDirectory }) property.Dispose();
        base.Dispose();
    }
}
