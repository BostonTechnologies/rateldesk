using System.CommandLine;
using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

public static class HostDeploymentCommands
{
    public static void Register(RootCommand root, TextWriter output, TextWriter error)
    {
        var host = new Command("host", "Read-only effective deployment preflight and approved-generation verification. Use the Docker host launcher to apply.");
        foreach (var action in new[] { "preflight", "verify", "approval-check", "observation-check", "export-check" })
        {
            var command = new Command(action, action == "verify" ? "Observe applied configuration and request a narrow temporary status verifier." : "Validate the complete proposed deployment before approval.");
            var directory = new Option<string?>("--export-directory"); var url = new Option<string?>("--url");
            var intentOption = new Option<string?>("--intent") { Description = "verify-existing, prepare-changes, or the separately reviewed legacy-initializer alternative" };
            command.Options.Add(directory); command.Options.Add(url); command.Options.Add(intentOption);
            command.SetAction(async (parse, ct) =>
            {
                try
                {
                    var path = parse.GetValue(directory) ?? SetupInput.HostDirectory;
                    if (action == "export-check")
                    {
                        var approved = HostDeploymentWorkflow.ResumeApproved(path);
                        var exported = HttpMcpEffectiveDeployment.Parse(SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(path, ".resolved-export.json")));
                        var diagnostics = HttpMcpEffectiveDeployment.ValidateExport(approved, exported);
                        if (diagnostics.Count > 0) throw new AgentClientValidationException(HostDeploymentWorkflow.Describe(diagnostics));
                        await output.WriteLineAsync("Complete reviewed export configuration and mount validation passed. No service was changed.");
                        return 0;
                    }
                    if (action == "approval-check")
                    {
                        await HostDeploymentWorkflow.CheckApplyAuthorityAsync(path, ct);
                        await output.WriteLineAsync("Current administrator approval and exact deployment generation confirmed. No service was changed.");
                        return 0;
                    }
                    if (action == "observation-check")
                    {
                        HostDeploymentWorkflow.CompleteObservation(path);
                        await output.WriteLineAsync("End-to-end verified. Fresh Docker observation, authenticated initialize, complete catalog, API-backed status and temporary access cleanup passed.");
                        return 0;
                    }
                    if (action == "verify")
                    {
                        if (!Console.IsInputRedirected && !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out))
                            return await SetupRunner.RunAsync(new(Hosting: true, OutputDirectory: path) { VerificationDirectory = path }, error, ct);
                        return await HostDeploymentWorkflow.VerifyAsync(path, output, error, ct);
                    }
                    var settings = HttpMcpEffectiveDeployment.Adopt(HttpMcpHostingCoordinator.Load(path));
                    if (parse.GetValue(url) is { } supplied) settings = settings with { PublicOrigin = SetupInput.NormalizeWebUrl(supplied) };
                    var intent = parse.GetValue(intentOption) switch
                    {
                        null => HttpMcpEffectiveDeployment.SelectedIntent,
                        "verify-existing" => HttpMcpHostIntent.VerifyExisting,
                        "prepare-changes" => HttpMcpHostIntent.PrepareChanges,
                        "legacy-initializer" => HttpMcpHostIntent.LegacyInitializer,
                        _ => throw new AgentClientValidationException("HOST_INTENT_INVALID: Select verify-existing, prepare-changes or legacy-initializer.")
                    };
                    var proposal = HttpMcpEffectiveDeployment.Create(settings, intent: intent);
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { valid = proposal.Valid, intent = proposal.Intent.ToString(), proposal.ServiceCountBefore, proposal.ServiceCountAfter, proposal.Changes, proposal.Diagnostics }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return proposal.Valid ? 0 : SetupRunner.ConfigurationError;
                }
                catch (Exception exception) when (exception is AgentClientValidationException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or JsonException)
                { await error.WriteLineAsync(exception is AgentClientValidationException ? TerminalText.Sanitize(exception.Message) : "Host operation failed. Check private persistent storage, the trusted Web URL and TLS; no completion was recorded."); return SetupRunner.ConfigurationError; }
                catch (OperationCanceledException) { await error.WriteLineAsync("Host verification stopped. Settings remain verify pending; bounded verifier cleanup will be retried on resume."); return 130; }
            });
            host.Subcommands.Add(command);
        }
        root.Subcommands.Add(host);
    }
}
