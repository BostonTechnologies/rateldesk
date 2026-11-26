using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed partial class AutomationBindingDriftService
{
    public async Task<AutomationInputReviewResultDto> ApplyReviewAsync(string bindingId,
        ApplyAutomationInputReviewDto request, CancellationToken cancellationToken = default)
    {
        if (request.Decisions is not { Count: > 0 and <= 128 } ||
            request.Decisions.Any(decision => decision is not { Key.Length: > 0 } ||
                !Enum.IsDefined(decision.Direction)) ||
            request.Decisions.Select(decision => decision.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Decisions.Count)
            throw new InvalidOperationException("Choose at most one action per reviewed input.");

        var binding = await QueryForTenant().SingleOrDefaultAsync(row => row.Id == bindingId, cancellationToken)
            ?? throw new InvalidOperationException("Automation binding was not found.");
        if (!binding.Enabled || binding.SystemConnectionId is not { } connectionId)
            throw new InvalidOperationException("Save an enabled binding with a NetRatel connection before applying input changes.");

        var formQuery = _db.RequestForms.IgnoreQueryFilters().Where(row => row.Id == binding.RequestFormId);
        var revision = await ConnectionRevisionAsync(binding, cancellationToken) ?? throw ReviewChanged();
        var remote = await ReadReviewTargetAsync(binding, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the saved form and selection before any peer write. Ordinary edits
        // use the same rows and cannot interleave with this apply.
        if (await formQuery.ExecuteUpdateAsync(update => update.SetProperty(row => row.Title, row => row.Title),
            cancellationToken) != 1)
            throw ReviewChanged();
        var form = await formQuery.SingleAsync(cancellationToken);
        var connectionFence = await _db.Set<SystemConnection>().Where(row =>
                row.Id == connectionId && row.State == "connected" && row.Revision == revision)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision), cancellationToken);
        if (connectionFence != 1) throw ReviewChanged();
        await AutomationBindingSelectionFence.SaveAsync(_db, [binding], () => { }, cancellationToken);
        if (await ConnectionRevisionAsync(binding, cancellationToken) != revision ||
            !string.Equals(ReviewVersion(binding, form, remote, revision), request.ReviewVersion, StringComparison.Ordinal))
            throw ReviewChanged();

        var plan = PlanReview(binding, form, remote, request.Decisions);
        cancellationToken.ThrowIfCancellationRequested();
        // Once a peer write can start, finish verification independently of the browser connection.
        // The catalogue client already bounds each outbound request.
        cancellationToken = CancellationToken.None;
        var reference = correlation?.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
        var hasPush = request.Decisions.Any(decision => decision.Direction == AutomationInputDirection.Push);
        try
        {
            if (hasPush)
                await _stoCatalogService.ApplyReviewedInputsAsync(binding.OrchestrationRequestDefinitionId,
                    remote.Inputs, plan.Inputs, binding.OrganizationId, connectionId, cancellationToken);

            remote = await ReadReviewTargetAsync(binding, cancellationToken);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan.Inputs), JsonSerializer.SerializeToElement(remote.Inputs)) ||
                await ConnectionRevisionAsync(binding, cancellationToken) != revision)
                return await RecordUnconfirmedAsync(binding, request, reference, transaction, cancellationToken);
        }
        catch (OrchestrationProviderException failure) when (failure.Error.Status == 409)
        {
            throw ReviewChanged();
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException or HttpRequestException ||
            failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return await RecordUnconfirmedAsync(binding, request, reference, transaction, cancellationToken);
        }

        if (plan.Schema is not null) form.JsonSchema = plan.Schema;
        await _db.SaveChangesAsync(cancellationToken);
        var verifiedForm = await formQuery.AsNoTracking().SingleAsync(cancellationToken);
        if (!JsonElement.DeepEquals(form.JsonSchema.RootElement, verifiedForm.JsonSchema.RootElement))
            throw new InvalidOperationException($"Helpdesk read-back was inconclusive. Reopen the review. Reference {reference}.");

        var preview = BuildPreview(binding, verifiedForm, remote);
        var synced = preview.Differences.All(difference => difference.ChangeType == "Unchanged");
        binding.SyncState = synced ? AutomationBindingSyncState.InSync : AutomationBindingSyncState.Drifted;
        binding.LastSyncHash = synced ? AutomationBindingSchemaHash.Compute(remote.Inputs) : null;
        binding.LastCorrelationId = reference;
        binding.LastReviewedDriftAtUtc = binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (synced)
        {
            binding.LastSyncedAtUtc = binding.UpdatedAtUtc;
            binding.LastSyncVersion = "schema:v1";
            binding.LastSyncDirection = "Reviewed";
        }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var outcomes = request.Decisions.Select(decision => new AutomationInputOutcomeDto(decision.Key,
            decision.Direction switch
            {
                AutomationInputDirection.Push => "Updated and verified in NetRatel.",
                AutomationInputDirection.Import => "Updated and verified in Helpdesk.",
                _ => "Left unresolved."
            })).ToList();
        return new(reference, synced, plan.Schema is not null, outcomes);
    }

    private async Task<AutomationInputReviewResultDto> RecordUnconfirmedAsync(AutomationBinding binding,
        ApplyAutomationInputReviewDto request, string reference, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        binding.SyncState = AutomationBindingSyncState.Drifted;
        binding.LastSyncHash = null;
        binding.LastCorrelationId = reference;
        binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(reference, false, false, request.Decisions.Select(decision => new AutomationInputOutcomeDto(decision.Key,
            decision.Direction switch
            {
                AutomationInputDirection.Push => "NetRatel write could not be confirmed. Recheck before retrying.",
                AutomationInputDirection.Import => "Helpdesk change was withheld pending NetRatel verification.",
                _ => "Left unresolved."
            })).ToList());
    }

    private async Task<OrchestrationCatalogRequestDefinitionDto> ReadReviewTargetAsync(AutomationBinding binding, CancellationToken ct)
        => (await _stoCatalogService.ListRequestDefinitionsAsync(binding.OrganizationId, binding.SystemConnectionId, ct))
            .SingleOrDefault(target => target.RequestDefinitionId == binding.OrchestrationRequestDefinitionId &&
                (binding.SystemConnectionId is null || string.IsNullOrWhiteSpace(binding.OrchestrationJobDefinitionId) ||
                    target.OrchestrationJobDefinitionId == binding.OrchestrationJobDefinitionId))
            ?? throw new InvalidOperationException("The saved NetRatel job is unavailable. Review the automation connection.");

    private Task<long?> ConnectionRevisionAsync(AutomationBinding binding, CancellationToken ct)
        => _db.Set<SystemConnection>().AsNoTracking()
            .Where(row => row.Id == binding.SystemConnectionId && row.State == "connected")
            .Select(row => (long?)row.Revision).SingleOrDefaultAsync(ct);

    private static string ReviewVersion(AutomationBinding binding, RequestForm form,
        OrchestrationCatalogRequestDefinitionDto remote, long revision)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding.Id, binding.OrganizationId, binding.RequestFormId, binding.TaskTemplateId,
            binding.SystemConnectionId, binding.Enabled, revision, binding.OrchestrationRequestDefinitionId,
            binding.OrchestrationJobDefinitionId, form.Title, FormOrganization = form.OrganizationId,
            form.AllowedOrganizationIds, form.JsonSchema, remote.Target, remote.TenantId, remote.Inputs
        })));

    private static IntegrationProviderConfigurationConflictException ReviewChanged()
        => new("The form, binding, connection or NetRatel inputs changed. Reopen the review before applying.",
            "automation_input_review_stale");

    private (JsonDocument? Schema, IReadOnlyList<OrchestrationCatalogInputDefinitionDto> Inputs) PlanReview(
        AutomationBinding binding, RequestForm form, OrchestrationCatalogRequestDefinitionDto remote,
        IReadOnlyList<AutomationInputDecisionDto> decisions)
    {
        var preview = BuildPreview(binding, form, remote);
        var differences = preview.Differences.Where(difference => difference.ChangeType != "Unchanged")
            .ToDictionary(difference => difference.Key, StringComparer.OrdinalIgnoreCase);
        var inputs = remote.Inputs.ToDictionary(input => input.Key.Trim(), StringComparer.OrdinalIgnoreCase);
        var document = JsonNode.Parse(form.JsonSchema.RootElement.GetRawText(), new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();
        var fields = document["fields"] as JsonArray ?? new JsonArray();
        var task = document["tasks"]!.AsArray().OfType<JsonObject>()
            .Single(node => Guid.TryParse(node["id"]?.ToString(), out var id) && id == binding.TaskTemplateId);
        var mapping = task["payloadMapping"] as JsonObject;
        var fieldIndexes = preview.AvailableFields.Select((field, index) => (field.Key, Index: index))
            .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.OrdinalIgnoreCase);
        var usedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = false;
        foreach (var decision in decisions.Where(decision => decision.Direction != AutomationInputDirection.LeaveUnresolved))
        {
            if (!differences.TryGetValue(decision.Key.Trim(), out var difference)) throw ReviewChanged();
            if (decision.Direction == AutomationInputDirection.Push)
            {
                var input = ProjectPush(difference, inputs.GetValueOrDefault(difference.Key), inputs.Values);
                if (!input.Required && remote.Target?.RequiredInputs.Contains(input.Key, StringComparer.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException($"'{input.Key}' must be required because this job uses it to select a target.");
                inputs[difference.Key] = input;
                continue;
            }

            mapping ??= new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            if (mapping.Parent is null) task["payloadMapping"] = mapping;
            ApplyReviewedImport(difference, decision, preview, fields, fieldIndexes, mapping, usedFields);
            if (fields.Parent is null) document["fields"] = fields;
            imported = true;
        }

        JsonDocument? proposed = imported ? JsonDocument.Parse(document.ToJsonString()) : null;
        var projected = inputs.Values.OrderBy(input => input.Order).ThenBy(input => input.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return (proposed, projected);
    }

    private static OrchestrationCatalogInputDefinitionDto ProjectPush(AutomationBindingInputDiffDto difference,
        OrchestrationCatalogInputDefinitionDto? remote, IEnumerable<OrchestrationCatalogInputDefinitionDto> inputs)
    {
        if (difference.Helpdesk is not { Type: not "missing-field" } field)
            throw new InvalidOperationException($"'{difference.Key}' needs a mapped Helpdesk field before it can be pushed.");
        if (difference.Orchestration is { } target && !CompatibleTypes(field, target))
            throw Incompatible(difference.Key);
        var choices = difference.Orchestration?.Type == "select" ? field.Choices : null;
        if (choices is not null && (choices.Count == 0 ||
            remote?.DefaultValue is { } defaultValue && !choices.Contains(defaultValue, StringComparer.Ordinal)))
            throw new InvalidOperationException($"'{difference.Key}' needs choices that include its saved NetRatel default.");
        return new()
        {
            Key = remote?.Key ?? difference.Key, Type = remote?.Type ?? field.Type, Label = field.Label, Required = field.Required,
            DefaultValue = remote?.DefaultValue, HelpText = remote?.HelpText, OptionsJson = choices is null ? remote?.OptionsJson : JsonSerializer.Serialize(choices),
            Order = remote?.Order ?? inputs.Select(input => input.Order).DefaultIfEmpty(-1).Max() + 1
        };
    }

    private static void ApplyReviewedImport(AutomationBindingInputDiffDto difference, AutomationInputDecisionDto decision,
        AutomationBindingDriftPreviewDto preview, JsonArray fields, Dictionary<string, int> indexes,
        JsonObject mapping, HashSet<string> usedFields)
    {
        var local = preview.AvailableFields;
        var remote = difference.Orchestration ?? throw new InvalidOperationException($"'{difference.Key}' has no NetRatel input to import.");
        var key = decision.FieldKey?.Trim();
        var existing = key is not null && indexes.TryGetValue(key, out var index) ? local[index] : null;
        if (decision.CreateField)
        {
            if (existing is not null || key is not null || indexes.ContainsKey(remote.Key) ||
                difference.Helpdesk is { Type: not "missing-field" } || local.Any(field =>
                    string.Equals(field.Label, remote.Label, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"'{remote.Key}' may already have a Helpdesk field. Map the existing field.");
            if (remote.Type is not ("text" or "textarea" or "number" or "boolean" or "select"))
                throw Incompatible(remote.Key);
            key = remote.Key;
            var field = new JsonObject { ["key"] = key, ["label"] = remote.Label,
                ["type"] = remote.Type == "select" ? "predefined" : remote.Type, ["required"] = remote.Required };
            ApplyChoiceValues(field, remote);
            fields.Add(field);
        }
        else
        {
            if (existing is null)
                throw new InvalidOperationException($"Choose the existing Helpdesk field for '{remote.Key}'.");
            if (!CompatibleTypes(existing, remote))
                throw Incompatible(remote.Key);
            if ((existing.Label != remote.Label || existing.Required != remote.Required ||
                remote.Type == "select" && !existing.Choices.SequenceEqual(remote.Choices)) &&
                preview.HelpdeskInputs.Any(input => !string.Equals(input.Key, remote.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(input.FieldKey, existing.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"'{existing.Key}' is already mapped to another input. Choose a separate field before changing its label or requiredness.");
            key = existing.Key;
            var field = fields[indexes[key]]!.AsObject();
            if (string.IsNullOrWhiteSpace(field["key"]?.ToString())) field["key"] = key;
            field["label"] = remote.Label;
            field["required"] = remote.Required;
            ApplyChoiceValues(field, remote);
        }
        if (!usedFields.Add(key!))
            throw new InvalidOperationException($"The Helpdesk field '{key}' is selected more than once in this review.");
        // Replace a case/whitespace variant of the same input key, preserving unrelated mappings.
        foreach (var prior in mapping.Select(entry => entry.Key).Where(prior =>
            string.Equals(prior.Trim(), remote.Key, StringComparison.OrdinalIgnoreCase)).ToList())
            mapping.Remove(prior);
        mapping[remote.Key] = key;
    }

    private static bool CompatibleTypes(AutomationBindingInputSnapshotDto field, AutomationBindingInputSnapshotDto input)
        => input.Type == "select"
            ? !field.MultiSelect && !field.UsesDataset && field.PresentationType == "predefined"
            : field.Type == input.Type || field.Type == "textarea" && input.Type == "text";

    private static IReadOnlyList<string> ReadChoiceValues(string? value)
    {
        if (value is null) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) is { } choices &&
                choices.All(choice => !string.IsNullOrWhiteSpace(choice)) &&
                choices.Distinct(StringComparer.Ordinal).Count() == choices.Count ? choices : [];
        }
        catch (JsonException) { return []; }
    }

    private static void ApplyChoiceValues(JsonObject field, AutomationBindingInputSnapshotDto remote)
    {
        if (remote.Type != "select") return;
        if (remote.Choices.Count == 0)
            throw new InvalidOperationException($"'{remote.Key}' needs valid NetRatel select choices before it can be imported.");
        field["predefinedOptions"] = JsonSerializer.SerializeToNode(remote.Choices);
    }

    private static InvalidOperationException Incompatible(string key)
        => new($"'{key}' has incompatible parameter types. Map a compatible field or change the parameter type before applying.");
}
