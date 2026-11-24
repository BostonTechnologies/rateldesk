using System.Globalization;
using System.Text.Json;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Workflow;

public sealed class WorkflowConditionEvaluator(ILogger<WorkflowConditionEvaluator> logger) : IWorkflowConditionEvaluator
{
    private static readonly string[] Operators = [">=", "<=", "==", "!=", ">", "<"];
    private readonly ILogger<WorkflowConditionEvaluator> _logger = logger;

    public bool Evaluate(Request request, RequestTask task)
    {
        var expression = task.ConditionExpression?.Trim();
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            _logger.LogWarning(
                "Condition evaluation failed due to empty payload. RequestId={RequestId} TaskId={TaskId} Expression={Expression}",
                request.Id,
                task.Id,
                expression);
            return false;
        }

        if (!TryParseExpression(expression, out var field, out var @operator, out var rightLiteral))
        {
            _logger.LogWarning(
                "Condition evaluation failed due to invalid expression format. RequestId={RequestId} TaskId={TaskId} Expression={Expression}",
                request.Id,
                task.Id,
                expression);
            return false;
        }

        try
        {
            using var payloadDocument = JsonDocument.Parse(request.PayloadJson);
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!payloadDocument.RootElement.TryGetProperty(field, out var payloadValue))
            {
                return false;
            }

            if (payloadValue.ValueKind == JsonValueKind.Array)
            {
                _logger.LogWarning("Workflow conditions require scalar fields. TaskId={TaskId} Field={Field}", task.Id, field);
                return false;
            }

            return Compare(payloadValue, @operator, rightLiteral);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Condition evaluation failed while parsing payload. RequestId={RequestId} TaskId={TaskId} Expression={Expression}",
                request.Id,
                task.Id,
                expression);
            return false;
        }
    }

    public static string? ValidateMultiSelectConditions(RequestFormSchemaModel schema)
    {
        var selectionKeys = schema.Fields.Select((field, index) => (Field: field, Key: FormFieldKeyResolver.Resolve(field, index)))
            .Where(pair => pair.Field.MultiSelect).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (selectionKeys.Count == 0)
        {
            return null;
        }

        foreach (var task in schema.Tasks)
        {
            if (task.ConditionExpression?.Trim() is not { Length: > 0 } condition)
            {
                continue;
            }

            if (!TryParseExpression(condition, out var field, out _, out _))
            {
                return $"Task '{task.Name}' has an unsupported condition. Use a comparison on a single-value field.";
            }

            if (selectionKeys.Contains(field))
            {
                return $"Task '{task.Name}' condition references multi-select field '{field}'. Choose a single-value field.";
            }
        }

        return null;
    }

    private static bool TryParseExpression(
        string expression,
        out string field,
        out string @operator,
        out string rightLiteral)
    {
        field = string.Empty;
        @operator = string.Empty;
        rightLiteral = string.Empty;

        foreach (var candidate in Operators)
        {
            var index = expression.IndexOf(candidate, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var left = expression[..index].Trim();
            var right = expression[(index + candidate.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            if (!left.StartsWith("payload.", StringComparison.Ordinal))
            {
                return false;
            }

            var extractedField = left["payload.".Length..].Trim();
            if (string.IsNullOrWhiteSpace(extractedField)
                || extractedField.Contains('.', StringComparison.Ordinal)
                || extractedField.Contains('(')
                || extractedField.Contains(')'))
            {
                return false;
            }

            field = extractedField;
            @operator = candidate;
            rightLiteral = right;
            return true;
        }

        return false;
    }

    private static bool Compare(JsonElement payloadValue, string @operator, string rightLiteral)
    {
        if (TryParseStringLiteral(rightLiteral, out var rightString))
        {
            if (payloadValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var leftString = payloadValue.GetString() ?? string.Empty;
            return @operator switch
            {
                "==" => string.Equals(leftString, rightString, StringComparison.Ordinal),
                "!=" => !string.Equals(leftString, rightString, StringComparison.Ordinal),
                _ => false
            };
        }

        if (bool.TryParse(rightLiteral, out var rightBool))
        {
            if (payloadValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            var leftBool = payloadValue.GetBoolean();
            return @operator switch
            {
                "==" => leftBool == rightBool,
                "!=" => leftBool != rightBool,
                _ => false
            };
        }

        if (!double.TryParse(rightLiteral, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber)
            || !TryReadNumber(payloadValue, out var leftNumber))
        {
            return false;
        }

        return @operator switch
        {
            "==" => leftNumber == rightNumber,
            "!=" => leftNumber != rightNumber,
            ">" => leftNumber > rightNumber,
            "<" => leftNumber < rightNumber,
            ">=" => leftNumber >= rightNumber,
            "<=" => leftNumber <= rightNumber,
            _ => false
        };
    }

    private static bool TryReadNumber(JsonElement payloadValue, out double result)
    {
        result = 0;
        return payloadValue.ValueKind == JsonValueKind.Number
               && payloadValue.TryGetDouble(out result);
    }

    private static bool TryParseStringLiteral(string literal, out string value)
    {
        value = string.Empty;
        if (literal.Length < 2 || literal[0] != '"' || literal[^1] != '"')
        {
            return false;
        }

        value = literal[1..^1];
        return true;
    }
}
