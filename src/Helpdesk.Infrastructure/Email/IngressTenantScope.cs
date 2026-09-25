using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Keeps ingress data access and captured email effects on the same authorized tenant.</summary>
internal static class IngressTenantScope
{
    public static void Bind(HelpdeskDbContext db, IIngressEffectContext effects, string organizationId)
    {
        db.RestrictIngressToOrganization(organizationId);
        effects.OrganizationId = organizationId;
    }

    public static IDisposable Adopt(HelpdeskDbContext db, IIngressEffectContext effects, string organizationId)
    {
        var previousDataOrganization = db.IngressOrganizationId;
        var previousEffectOrganization = effects.OrganizationId;
        Bind(db, effects, organizationId);
        return new Restore(db, effects, previousDataOrganization, previousEffectOrganization);
    }

    private sealed class Restore(HelpdeskDbContext db, IIngressEffectContext effects,
        string? dataOrganization, string? effectOrganization) : IDisposable
    {
        public void Dispose()
        {
            db.RestoreIngressOrganization(dataOrganization);
            effects.OrganizationId = effectOrganization;
        }
    }
}
