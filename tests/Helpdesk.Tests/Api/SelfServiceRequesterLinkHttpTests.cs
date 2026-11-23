using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Api;

public sealed class SelfServiceRequesterLinkHttpTests
{
    private const string Issuer = "https://identity.example.invalid/application";
    private const string Subject = "synthetic-subject";
    private const string ProviderUserId = "synthetic-provider-user";

    [Theory]
    [InlineData("authentik_user_id")]
    [InlineData("ak_user_id")]
    public async Task Disabled_provider_id_only_link_denies_context_and_direct_submission_without_writes(string claim)
    {
        await using var h = await CreateAsync(claim);
        var linkId = Guid.NewGuid().ToString("N");
        var disabledAt = DateTimeOffset.UtcNow;
        await h.EditAsync(db =>
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                Id = linkId, CustomerId = h.CustomerId, AuthentikUserId = ProviderUserId,
                InviteStatus = CustomerInviteStatus.Disabled, DisabledAtUtc = disabledAt
            });
            return Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=legacy-form");
        // The access policy rejects the disabled identity before either handler
        // can produce a submission-context or requester-error response body.
        Assert.Equal(HttpStatusCode.Forbidden, context.StatusCode);
        await AssertUnchangedAsync(h, 1);

        using var submission = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
        Assert.Equal(HttpStatusCode.Forbidden, submission.StatusCode);
        await AssertUnchangedAsync(h, 1);
        await h.EditAsync(async db =>
        {
            var link = await db.CustomerAuthLinks.SingleAsync();
            Assert.Equal(linkId, link.Id);
            Assert.Equal(h.CustomerId, link.CustomerId);
            Assert.Equal(ProviderUserId, link.AuthentikUserId);
            Assert.Equal(CustomerInviteStatus.Disabled, link.InviteStatus);
            Assert.Equal(disabledAt, link.DisabledAtUtc);
            Assert.Null(link.OidcIssuer);
            Assert.Null(link.OidcSubject);
        });
    }

    [Theory]
    [InlineData("authentik_user_id", true)]
    [InlineData("authentik_user_id", false)]
    [InlineData("ak_user_id", true)]
    [InlineData("ak_user_id", false)]
    public async Task Enabled_provider_id_only_link_keeps_its_trusted_requester_and_organization(string claim, bool withSubmissionKey)
    {
        await using var h = await CreateAsync(claim);
        await h.EditAsync(db =>
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, AuthentikUserId = ProviderUserId,
                InviteStatus = CustomerInviteStatus.Active
            });
            return Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=legacy-form");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        var preview = await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>();
        Assert.True(preview!.CanSubmit);
        Assert.Equal(h.OrganizationId, preview.OrganizationId);
        await AssertUnchangedAsync(h, 1);

        var submission = Submission(); if (!withSubmissionKey) submission.SubmissionId = null;
        using var submitted = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", submission);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        await AssertSubmittedAsync(h);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Active_higher_priority_link_is_not_overridden_by_a_disabled_matching_alias(bool localAccount)
    {
        await using var h = await CreateAsync("authentik_user_id", localAccount);
        await h.EditAsync(db =>
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, AuthProviderType = localAccount ? "Local" : "Authentik",
                LocalAccountId = localAccount ? "owner" : null,
                OidcIssuer = localAccount ? null : Issuer, OidcSubject = localAccount ? null : Subject,
                InviteStatus = CustomerInviteStatus.Active
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, AuthentikUserId = ProviderUserId,
                OidcIssuer = localAccount ? Issuer : null, OidcSubject = localAccount ? Subject : null,
                InviteStatus = CustomerInviteStatus.Disabled, DisabledAtUtc = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=legacy-form");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        Assert.True((await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>())!.CanSubmit);
        await AssertUnchangedAsync(h, 2);

        using var submitted = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        await AssertSubmittedAsync(h);
        await h.EditAsync(async db => Assert.Single(await db.CustomerAuthLinks.Where(link => link.DisabledAtUtc != null).ToListAsync()));
    }

    [Fact]
    public async Task Changed_customer_binding_denies_a_prior_access_snapshot_without_writes()
    {
        await using var h = await CreateAsync("authentik_user_id", priorCustomerId: "prior-customer");
        await h.EditAsync(db =>
        {
            db.Customers.Add(new Customer { Id = "prior-customer", Name = "Prior requester", Email = "prior@example.invalid", OrganizationId = h.OrganizationId });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, AuthentikUserId = ProviderUserId, InviteStatus = CustomerInviteStatus.Active
            });
            return Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=legacy-form");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        var preview = await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>();
        Assert.False(preview!.CanSubmit);
        Assert.Equal("requester_unavailable", preview.ErrorCode);
        using var submitted = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
        Assert.Equal(HttpStatusCode.Forbidden, submitted.StatusCode);
        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Requests.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RequestTasks.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(2, await db.Customers.CountAsync());
            var link = await db.CustomerAuthLinks.SingleAsync();
            Assert.Equal(h.CustomerId, link.CustomerId);
            Assert.Equal(CustomerInviteStatus.Active, link.InviteStatus);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_oidc_identity_uses_both_grants_and_keeps_its_canonical_contact(bool postgres)
    {
        await using var h = await CreateAsync("authentik_user_id", postgres: postgres);
        const string secondary = "oidc-secondary";
        await h.EditAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = secondary, Name = "Secondary organization" });
            db.Users.Add(new User { Id = "oidc-domain", OrganizationId = h.OrganizationId, Name = "OIDC requester", Email = "oidc@example.test" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, OidcIssuer = Issuer, OidcSubject = Subject, DomainUserId = "oidc-domain",
                InviteStatus = CustomerInviteStatus.Active
            });
            foreach (var id in new[] { h.OrganizationId, secondary })
                db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "oidc-domain", OrganizationId = id, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.RequestForms.Add(new RequestForm
            {
                Id = "oidc-secondary-form", Title = "Secondary request", OrganizationId = secondary,
                AllowedOrganizationIds = [secondary], ReleaseStatus = RequestFormReleaseStatus.Production
            });
            await Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=oidc-secondary-form");
        Assert.Equal(secondary, (await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>())!.OrganizationId);
        await h.EditAsync(async db => Assert.Single(await db.Customers.ToListAsync()));

        using var primaryCreated = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
        Assert.Equal(HttpStatusCode.OK, primaryCreated.StatusCode);
        using var secondaryCreated = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "oidc-secondary-form", SubmissionId = Guid.NewGuid(), TargetOrganizationId = secondary });
        Assert.Equal(HttpStatusCode.OK, secondaryCreated.StatusCode);
        using var history = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/requests");
        var receipts = (await history.Content.ReadFromJsonAsync<PagedResponse<MyRequestListItemDto>>())!;
        Assert.Equal(2, receipts.TotalCount);

        await h.EditAsync(async db =>
        {
            var link = Assert.Single(await db.CustomerAuthLinks.ToListAsync());
            Assert.Equal(h.CustomerId, link.CustomerId);
            Assert.Equal(Subject, link.OidcSubject);
            Assert.Null(link.LocalAccountId);
            var requester = Assert.Single(await db.Customers.Where(customer => customer.IdentityUserId == "oidc-domain").ToListAsync());
            Assert.Equal(secondary, requester.OrganizationId);
            Assert.Equal(secondary, (await db.Requests.IgnoreQueryFilters().SingleAsync(request => request.CustomerId == requester.Id)).OrganizationId);
        });
    }

    private static SubmitSelfServiceRequestDto Submission() => new()
    {
        RequestFormId = "legacy-form", SubmissionId = Guid.NewGuid(), PayloadJson = "{}",
        RequesterEmail = "spoof@example.invalid"
    };

    private static async Task AssertUnchangedAsync(IncidentReceiverTests.Harness h, int links)
        => await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Requests.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RequestTasks.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(h.CustomerId, (await db.Customers.SingleAsync()).Id);
            Assert.Null((await db.Customers.SingleAsync()).IdentityUserId);
            Assert.Equal(links, await db.CustomerAuthLinks.CountAsync());
        });

    private static async Task AssertSubmittedAsync(IncidentReceiverTests.Harness h)
        => await h.EditAsync(async db =>
        {
            var request = await db.Requests.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(h.CustomerId, request.CustomerId);
            Assert.Equal(h.OrganizationId, request.OrganizationId);
            Assert.Equal((await db.Customers.SingleAsync()).Email, request.RequesterEmail);
            Assert.NotNull(request.SelfServiceSubmitterId);
            Assert.Empty(await db.RequestTasks.IgnoreQueryFilters().ToListAsync());
        });

    private static async Task<HttpResponseMessage> SendAsync(IncidentReceiverTests.Harness h,
        HttpMethod method, string path, object? payload = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = payload is null ? null : JsonContent.Create(payload)
        };
        request.Headers.Add("X-Synthetic-Requester", "true");
        return await h.Client.SendAsync(request);
    }

    private static async Task<IncidentReceiverTests.Harness> CreateAsync(string legacyClaim, bool localAccount = false, string? priorCustomerId = null, bool postgres = false)
    {
        var h = await IncidentReceiverTests.Harness.CreateAsync(postgres: postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddScoped<SelfServiceAudienceService>();
                services.AddSingleton(new RequesterClaims(legacyClaim, localAccount, priorCustomerId));
                if (priorCustomerId is not null) services.AddScoped<ICurrentUserAccessService, PriorRequesterAccess>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "RequesterRouting";
                        options.DefaultChallengeScheme = "RequesterRouting";
                    })
                    .AddPolicyScheme("RequesterRouting", "RequesterRouting", options => options.ForwardDefaultSelector = context =>
                        context.Request.Headers.ContainsKey("X-Synthetic-Requester") ? "Requester" : "ReceiverTest")
                    .AddScheme<AuthenticationSchemeOptions, RequesterAuthentication>("Requester", _ => { });
                services.AddAuthorization(options => options.AddPolicy("SelfService.User", policy =>
                    policy.RequireRole("SelfService.User", "HelpdeskAdmin")));
            }, configureApp: app =>
            {
                app.MapSelfServiceRequestEndpoints();
                app.MapSelfServiceMyRequestsEndpoints();
            });
        await h.EditAsync(db =>
        {
            db.RequestForms.Add(new RequestForm
            {
                Id = "legacy-form", AllowedOrganizationIds = [h.OrganizationId], Title = "Synthetic legacy request", OrganizationId = h.OrganizationId,
                ReleaseStatus = RequestFormReleaseStatus.Production,
                JsonSchema = JsonDocument.Parse("""{"title":"Synthetic legacy request","fields":[],"tasks":[]}""")
            });
            return Task.CompletedTask;
        });
        return h;
    }

    private sealed record RequesterClaims(string LegacyClaim, bool LocalAccount, string? PriorCustomerId);

    // Represents access resolved immediately before the identity link changed.
    // Authentication, organization permissions and link lookup still use the real resolver.
    private sealed class PriorRequesterAccess(HelpdeskDbContext db, RatelDeskIdentityDbContext identity) : ICurrentUserAccessService
    {
        public async Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var current = await new CurrentUserAccessService(db, identity).ResolveAsync(user, ct);
            var prior = user.FindFirstValue("synthetic_prior_customer");
            return prior is null ? current : current with { CustomerId = prior };
        }
    }

    private sealed class RequesterAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, RequesterClaims requester)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            List<Claim> claims =
            [
                new(ClaimTypes.NameIdentifier, requester.LocalAccount ? "owner" : Subject),
                new("auth_mode", requester.LocalAccount ? "local" : "oidc"),
                new(ClaimTypes.Email, "claim-only@example.invalid"),
                new("iss", Issuer), new("sub", Subject), new(requester.LegacyClaim, ProviderUserId)
            ];
            if (requester.PriorCustomerId is not null)
                claims.Add(new Claim("synthetic_prior_customer", requester.PriorCustomerId));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
