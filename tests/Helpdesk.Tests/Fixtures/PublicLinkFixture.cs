using System.Net.Http.Json;
using System.Security.Cryptography;
using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.DTOs.PublicAccess;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Fixtures;

internal static class PublicLinkFixture
{
    internal static void AddServices(WebApplicationBuilder builder)
    {
        PublicAdmission.Configure(builder);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IPublicTicketLinkSigner>(new PublicTicketLinkSigner(
            Options.Create(new StorageOptions { ImageSigningSecret = "synthetic-legacy-link-signing-key" }),
            NullLogger<PublicTicketLinkSigner>.Instance));
        builder.Services.AddScoped<PublicLinkService>();
        builder.Services.TryAddScoped<MailboxSenderResolver>();
        builder.Services.AddScoped<IPublicLinkIssuer>(services => services.GetRequiredService<PublicLinkService>());
        builder.Services.AddSingleton<PublicTicketAssets>();
    }

    internal static string Browser() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal static async Task<string> IssueAsync(IServiceProvider services, string ticketId, string recipient = "customer@example.test",
        PublicLinkPurpose purpose = PublicLinkPurpose.Ticket, string? task = null)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var ticket = await db.Tickets.IgnoreQueryFilters().SingleAsync(row => row.Id == ticketId);
        var links = scope.ServiceProvider.GetRequiredService<PublicLinkService>();
        var url = await links.IssueAsync(ticket, recipient, purpose, "https://desk.example.test", default, task);
        return url.Split('#')[1];
    }

    internal static async Task<PublicLinkExchangeResponse> ExchangeAsync(IServiceProvider services, string launch)
    {
        await using var scope = services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<PublicLinkService>();
        return Assert.IsType<PublicLinkExchangeResponse>(await links.ExchangeAsync(new(launch, null, Browser()), default));
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, PublicLinkExchangeResponse grant, string resource,
        PublicApprovalDecision? decision = null)
    {
        using var request = new HttpRequestMessage(decision is null ? HttpMethod.Get : HttpMethod.Post,
            $"{PublicAccessProtocol.ApiPrefix}/{grant.AccessId:D}/{resource}");
        request.Headers.Add(PublicAccessProtocol.GrantHeader, grant.Grant);
        if (decision is not null) request.Content = JsonContent.Create(decision);
        return await client.SendAsync(request);
    }
}
