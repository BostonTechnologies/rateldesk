using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Services;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
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
        await h.EditAsync(db =>
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = h.CustomerId, AuthentikUserId = ProviderUserId,
                InviteStatus = CustomerInviteStatus.Disabled, DisabledAtUtc = DateTimeOffset.UtcNow
            });
            return Task.CompletedTask;
        });

        using var context = await SendAsync(h, HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=legacy-form");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        var preview = await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>();
        Assert.False(preview!.CanSubmit);
        Assert.Equal("requester_unavailable", preview.ErrorCode);
        await AssertUnchangedAsync(h, 1);

        using var submission = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
        Assert.Equal(HttpStatusCode.Forbidden, submission.StatusCode);
        Assert.Contains("requester_unavailable", await submission.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(h, 1);
        await h.EditAsync(async db =>
        {
            var link = await db.CustomerAuthLinks.SingleAsync();
            Assert.Equal(CustomerInviteStatus.Disabled, link.InviteStatus);
            Assert.NotNull(link.DisabledAtUtc);
            Assert.Null(link.OidcIssuer);
            Assert.Null(link.OidcSubject);
        });
    }

    [Theory]
    [InlineData("authentik_user_id")]
    [InlineData("ak_user_id")]
    public async Task Enabled_provider_id_only_link_keeps_its_trusted_requester_and_organization(string claim)
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

        using var submitted = await SendAsync(h, HttpMethod.Post, "/api/v1/self-service/requests", Submission());
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

    private static async Task<IncidentReceiverTests.Harness> CreateAsync(string legacyClaim, bool localAccount = false)
    {
        var h = await IncidentReceiverTests.Harness.CreateAsync(postgres: false, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
                services.AddSingleton(new RequesterClaims(legacyClaim, localAccount));
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
            }, configureApp: app => app.MapSelfServiceRequestEndpoints());
        await h.EditAsync(db =>
        {
            db.RequestForms.Add(new RequestForm
            {
                Id = "legacy-form", Title = "Synthetic legacy request", OrganizationId = h.OrganizationId,
                ReleaseStatus = RequestFormReleaseStatus.Production,
                JsonSchema = JsonDocument.Parse("""{"title":"Synthetic legacy request","fields":[],"tasks":[]}""")
            });
            return Task.CompletedTask;
        });
        return h;
    }

    private sealed record RequesterClaims(string LegacyClaim, bool LocalAccount);

    private sealed class RequesterAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, RequesterClaims requester)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, requester.LocalAccount ? "owner" : Subject),
                new("auth_mode", requester.LocalAccount ? "local" : "oidc"),
                new(ClaimTypes.Email, "claim-only@example.invalid"),
                new("iss", Issuer), new("sub", Subject), new(requester.LegacyClaim, ProviderUserId)
            ];
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
