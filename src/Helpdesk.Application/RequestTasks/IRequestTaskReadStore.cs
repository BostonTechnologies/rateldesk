using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.RequestTasks;

public interface IRequestTaskReadStore
{
    Task<IReadOnlyList<RequestTask>> GetForRequestAsync(string requestId, CancellationToken ct);
}

internal static class RequestTaskQueries
{
    public static async Task<List<RequestTask>> ForRequestAsync(IRepository<RequestTask> repository,
        IRequestTaskReadStore? reader, string requestId, CancellationToken ct)
        => reader is not null ? (await reader.GetForRequestAsync(requestId, ct)).ToList()
            : (await repository.GetAllAsync()).Where(task => task.RequestId == requestId).ToList();
}
