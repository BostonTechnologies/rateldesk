using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

internal static partial class HelpdeskCli
{
    private static Command BuildAuthCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var auth = new Command("auth", "Configure and verify Authentik or integration-credential authentication.");

        var configure = new Command("configure", "Persist local CLI authentication settings.");
        configure.SetHandler(async ctx =>
        {
            var config = LoadConfig(ctx, globals, requireAuth: false, allowMissingFile: true);
            var store = new CliConfigStore(SelectedConfigurationPath(ctx, globals));
            store.Save(config.File.Merge(config.Overrides));
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        auth.AddCommand(configure);

        var status = new Command("status", "Call the current credential's Helpdesk API status endpoint.");
        status.SetHandler(async ctx =>
        {
            var resolved = LoadConfig(ctx, globals).Resolved!;
            var response = await SendRawAsync(runtime, resolved, HttpMethod.Get, resolved.CredentialMode == "integration" ? "/api/v1/auth/me" : "/api/v1/auth/ai-agent/status", authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
        });
        auth.AddCommand(status);

        var token = new Command("token", "Mint and print an Authentik AI-agent bearer token.");
        token.SetHandler(async ctx =>
        {
            var resolved = LoadConfig(ctx, globals).Resolved;
            var accessToken = await runtime.GetAccessTokenAsync(resolved!, ctx.GetCancellationToken()).ConfigureAwait(false);
            if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                await runtime.Out.WriteLineAsync(accessToken).ConfigureAwait(false);
            }
        });
        auth.AddCommand(token);
        var logout = new Command("logout", "Remove local authentication references; server revocation remains separate.");
        logout.SetHandler(async ctx =>
        {
            var selected = SelectedConfigurationPath(ctx, globals);
            var resolution = ResolveLocalConfiguration(ctx, globals);
            if (resolution.Settings.Any(setting => setting.Managed && setting.IsSecret))
                throw new CliValidationException("Authentication is externally managed; change the owning environment or secret mount.");
            var store = new Helpdesk.AgentClient.AgentClientConfigurationStore(selected);
            var value = store.Load() with { IntegrationCredential = null, IntegrationCredentialFile = null, AuthentikAppPassword = null, AuthentikAppPasswordFile = null };
            store.SaveSecure(value, resolution.Revision ?? "", ctx.GetCancellationToken());
            await WriteJsonAsync(runtime, new { loggedOutLocally = true, serverCredentialRevoked = false }, ctx, globals);
        });
        auth.AddCommand(logout);

        return auth;
    }

    private static Command BuildConfigCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var config = new Command("config", "Read and edit local Helpdesk CLI config.");
        config.AddCommand(BuildLocalValidateCommand(runtime, globals, "validate"));
        var keyArg = new Argument<string>("key");
        var valueArg = new Argument<string>("value");

        var show = new Command("show", "Show merged CLI config with secrets redacted.");
        show.SetHandler(ctx =>
        {
            var resolution = ResolveLocalConfiguration(ctx, globals);
            var redacted = JsonSerializer.SerializeToNode(Redact(CliConfig.FromAgentConfiguration(resolution.Configuration)), JsonOptions)!.AsObject();
            redacted["sources"] = JsonSerializer.SerializeToNode(resolution.Settings, JsonOptions);
            return WriteJsonAsync(runtime, redacted, ctx, globals);
        });
        config.AddCommand(show);

        var get = new Command("get", "Read one persisted config value.") { keyArg };
        get.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(SelectedConfigurationPath(ctx, globals));
            var key = RequiredArgument(ctx.ParseResult, keyArg);
            var value = GetConfigValue(store.Load(), key, redact: true);
            await WriteJsonAsync(runtime, new { key, value }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(get);

        var set = new Command("set", "Set one persisted config value.") { keyArg, valueArg };
        set.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(SelectedConfigurationPath(ctx, globals));
            var current = store.Load();
            var updated = SetConfigValue(current, RequiredArgument(ctx.ParseResult, keyArg), RequiredArgument(ctx.ParseResult, valueArg));
            store.Save(updated);
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(set);

        var unset = new Command("unset", "Unset one persisted config value.") { keyArg };
        unset.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(SelectedConfigurationPath(ctx, globals));
            var updated = SetConfigValue(store.Load(), RequiredArgument(ctx.ParseResult, keyArg), null);
            store.Save(updated);
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(unset);

        return config;
    }

    private static Command BuildHealthCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("health", "Check live, ready, and authenticated AI-agent status.");
        command.SetHandler(async ctx =>
        {
            var loaded = LoadConfig(ctx, globals);
            var authPath = loaded.Resolved!.CredentialMode == "integration" ? "/api/v1/auth/me" : "/api/v1/auth/ai-agent/status";
            var live = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/live", authenticated: false, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var ready = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/ready", authenticated: false, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var auth = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, authPath, authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            await WriteJsonAsync(runtime, new[] { live.ToHealth("live"), ready.ToHealth("ready"), auth.ToHealth("auth") }, ctx, globals).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildLogsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var logs = new Command("logs", "Search Helpdesk API AI-agent operation logs.");
        var search = new Command("search", "Search log entries.");
        var tail = new Command("tail", "Poll log entries repeatedly.");
        var since = new Option<long?>("--since") { Description = "Return logs after this sequence." };
        var level = new Option<string?>("--level") { Description = "Filter by log level." };
        var contains = new Option<string?>("--contains") { Description = "Filter by message text." };
        var correlationId = new Option<string?>("--correlation-id") { Description = "Filter by correlation id." };
        var limit = new Option<int?>("--limit") { Description = "Maximum entries." };
        foreach (var option in new Option[] { since, level, contains, correlationId, limit }) search.AddOption(option);
        search.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/ops/ai-agent/logs" + Query(ctx, (since, "since"), (level, "level"), (contains, "contains"), (correlationId, "correlationId"), (limit, "limit"))));
        logs.AddCommand(search);

        var iterations = new Option<int>("--iterations") { Description = "Poll count.", DefaultValueFactory = _ => 20 };
        var delay = new Option<int>("--delay-seconds") { Description = "Delay between polls.", DefaultValueFactory = _ => 3 };
        foreach (var option in new Option[] { since, iterations, delay }) tail.AddOption(option);
        tail.SetHandler(async ctx =>
        {
            long? cursor = ctx.ParseResult.GetValueForOption(since);
            var count = Math.Max(1, ctx.ParseResult.GetValueForOption(iterations));
            var pause = Math.Max(1, ctx.ParseResult.GetValueForOption(delay));
            for (var i = 0; i < count; i++)
            {
                var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/ops/ai-agent/logs" + Query(("since", cursor?.ToString())), null).ConfigureAwait(false);
                if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
                {
                    await runtime.Out.WriteLineAsync(response.Body).ConfigureAwait(false);
                }
                cursor = ExtractNextSince(response.Body) ?? cursor;
                if (i + 1 < count)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), ctx.GetCancellationToken()).ConfigureAwait(false);
                }
            }
        });
        logs.AddCommand(tail);

        return logs;
    }

}
