using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.Sla;

public class RecipientResolver(IRepository<User> users) : IRecipientResolver
{
    public async Task<List<string>> ResolveEmailsAsync(string tenantId, List<RecipientTarget> targets, CancellationToken ct = default)
    {
        _ = ct;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allUsers = await users.GetAllAsync();
        var scopedUsers = allUsers
            .Where(x => !x.IsDeleted && string.Equals(x.OrganizationId, tenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Value))
            {
                continue;
            }

            switch (target.Type)
            {
                case RecipientTargetType.Email:
                    result.Add(target.Value.Trim());
                    break;
                case RecipientTargetType.Role:
                    foreach (var user in scopedUsers.Where(x => string.Equals(x.Role, target.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!string.IsNullOrWhiteSpace(user.Email))
                        {
                            result.Add(user.Email.Trim());
                        }
                    }

                    break;
                case RecipientTargetType.Group:
                    // Group resolution can be plugged in later when a group membership source exists.
                    break;
            }
        }

        return result.ToList();
    }
}
