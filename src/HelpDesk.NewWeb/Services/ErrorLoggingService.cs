using System.Net.Http.Json;
using Helpdesk.Shared.DTOs.Logging;

namespace HelpDesk.NewWeb.Services;

public interface IErrorLoggingService
{
    Task LogAsync(Exception ex, string? page = null);
    Task LogAsync(string message, string? stackTrace = null, string? page = null);
}

public class ErrorLoggingService(HttpClient httpClient, ILogger<ErrorLoggingService> logger) : IErrorLoggingService
{
    private readonly HttpClient _httpClient = httpClient;

    public Task LogAsync(Exception ex, string? page = null) =>
        LogAsync(ex.Message, ex.StackTrace, page);

    public async Task LogAsync(string message, string? stackTrace = null, string? page = null)
    {
        var entry = new LogEntryDto
        {
            Message = message,
            StackTrace = stackTrace,
            Page = page
        };
        try
        {
            var resp = await _httpClient.PostAsJsonAsync("/api/v1/errors", entry);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Error record delivery failed with HTTP status {StatusCode}.", (int)resp.StatusCode);
            }
        }
        catch (Exception)
        {
            logger.LogWarning("Error record delivery failed before a response was received.");
        }
    }
}
