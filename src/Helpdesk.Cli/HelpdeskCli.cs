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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int DefaultAggregatePageSize = 100;
    private const int DefaultAggregateMaxItems = 1000;
    private const int DefaultLatestUpdatedCount = 10;
    private const int DefaultTopCount = 5;
    private const int DefaultArrayDisplayCap = 100;
    private const int DefaultBodyLines = 5;
    private const int DefaultTruncate = 500;

    public static Task<int> RunAsync(string[] args) => RunAsync(args, new CliRuntime());

    internal static async Task<int> RunAsync(string[] args, CliRuntime runtime)
    {
        try
        {
            var parseResult = BuildRoot(runtime).Parse(args);
            return await parseResult.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                Output = runtime.Out,
                Error = runtime.Error
            }).ConfigureAwait(false);
        }
        catch (CliValidationException ex)
        {
            await WriteErrorAsync(runtime, "validation_error", ex.Message, CliExitCodes.ValidationError, details: ex.Details).ConfigureAwait(false);
            return CliExitCodes.ValidationError;
        }
        catch (CliRemoteException ex)
        {
            await WriteErrorAsync(runtime, ex.Code, ex.Message, ex.StatusCode, ex.ResponseBody).ConfigureAwait(false);
            return ex.StatusCode is 401 or 403 ? CliExitCodes.AuthError : CliExitCodes.RemoteError;
        }
        catch (HttpRequestException ex)
        {
            await WriteErrorAsync(runtime, "remote_request_failed", ex.Message, CliExitCodes.RemoteError).ConfigureAwait(false);
            return CliExitCodes.RemoteError;
        }
        catch (TaskCanceledException)
        {
            await WriteErrorAsync(runtime, "remote_timeout", "Remote request timed out.", CliExitCodes.RemoteError).ConfigureAwait(false);
            return CliExitCodes.RemoteError;
        }
    }

    internal static RootCommand BuildRoot(CliRuntime runtime)
    {
        var globals = new GlobalOptions();
        var root = new RootCommand("Helpdesk CLI for AI agents and operators.")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        globals.AddTo(root);
        root.AddCommand(Helpdesk.Setup.SetupCommands.CreateSetupCommand(Helpdesk.Setup.SetupTransport.Cli, globals.Profile, globals.ConfigPath, runtime.Error));
        root.AddCommand(BuildLocalStatusCommand(runtime, globals));
        root.AddCommand(BuildLocalValidateCommand(runtime, globals, "config-validate"));
        root.AddCommand(BuildDoctorCommand(runtime, globals));
        root.AddCommand(BuildProfileCommand(runtime, globals));
        root.AddCommand(BuildAuthCommand(runtime, globals));
        root.AddCommand(BuildConfigCommand(runtime, globals));
        root.AddCommand(BuildHealthCommand(runtime, globals));
        root.AddCommand(BuildLogsCommand(runtime, globals));
        root.AddCommand(BuildIncidentsCommand(runtime, globals));
        root.AddCommand(BuildRequestsCommand(runtime, globals));
        root.AddCommand(BuildChangesCommand(runtime, globals));
        root.AddCommand(BuildRequestTasksCommand(runtime, globals));
        root.AddCommand(BuildTicketsCommand(runtime, globals));
        root.AddCommand(BuildOrganizationsCommand(runtime, globals));
        root.AddCommand(BuildCustomersCommand(runtime, globals));
        root.AddCommand(BuildUsersCommand(runtime, globals));
        root.AddCommand(BuildCrudCommand(runtime, globals, "roles", "Manage permission-aware role definitions.", "/api/v1/admin/role-definitions"));
        root.AddCommand(BuildCategoriesCommand(runtime, globals));
        root.AddCommand(BuildServicesCommand(runtime, globals));
        root.AddCommand(BuildRequestFormsCommand(runtime, globals));
        root.AddCommand(BuildSelfServiceCommand(runtime, globals));
        root.AddCommand(BuildNotificationsCommand(runtime, globals));
        root.AddCommand(BuildSearchCommand(runtime, globals));
        root.AddCommand(BuildConnectivityCommand(runtime, globals));
        root.AddCommand(BuildSystemCommand(runtime, globals));
        root.AddCommand(BuildCapabilitiesCommand(runtime, globals));
        root.AddCommand(BuildSchemaCommand(runtime, globals));
        root.AddCommand(BuildEnumsCommand(runtime, globals));
        root.AddCommand(BuildExamplesCommand(runtime, globals));
        root.AddCommand(BuildRawCommand(runtime, globals));
        return root;
    }

}
