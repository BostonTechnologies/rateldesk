using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed class SelfServiceSubmissionIdentityHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unlinked_local_account_uses_durable_identity_and_retries_one_submission(bool postgres, bool administrator)
    {
        await using var h = await CreateAsync(postgres, administrator);
        await h.EditAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = "foreign", Name = "Other organization" });
            db.Customers.Add(new Customer { Id = "foreign-email", Name = "Unrelated", Email = "owner@example.test", OrganizationId = administrator ? "foreign" : h.OrganizationId });
            await Task.CompletedTask;
        });
        using var context = await h.AdminAsync(HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=self-form");
        var preview = (await context.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>())!;
        Assert.True(preview.CanSubmit);
        Assert.Equal(h.OrganizationId, preview.OrganizationId);
        await h.EditAsync(async db => Assert.Empty(await db.CustomerAuthLinks.ToListAsync()));

        var dto = new SubmitSelfServiceRequestDto { SubmissionId = Guid.NewGuid(), RequestFormId = "self-form", PayloadJson = "{}", RequesterEmail = "spoof@example.test" };
        using var first = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", dto);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<SelfServiceRequestSubmittedDto>())!;
        using var repeat = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", dto);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(receipt.RequestId, (await repeat.Content.ReadFromJsonAsync<SelfServiceRequestSubmittedDto>())!.RequestId);
        await h.EditAsync(async db =>
        {
            var request = Assert.Single(await db.Requests.ToListAsync());
            var link = Assert.Single(await db.CustomerAuthLinks.ToListAsync());
            var customer = await db.Customers.SingleAsync(x => x.Id == link.CustomerId);
            Assert.Equal("owner", link.LocalAccountId);
            Assert.Equal("owner", link.DomainUserId);
            Assert.Equal(h.OrganizationId, customer.OrganizationId);
            Assert.Equal(customer.Id, request.CustomerId);
            Assert.Equal("owner", customer.IdentityUserId);
            Assert.NotEqual("foreign-email", customer.Id);
            Assert.Equal("owner@example.test", request.RequesterEmail);
            Assert.Equal(dto.SubmissionId!.Value.ToString("N"), request.SelfServiceSubmissionId);
        });
        dto.PayloadJson = "{\"changed\":true}";
        using var changed = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", dto);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("submission_already_created", await changed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_organization_is_actionable_and_creates_no_link_or_request(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        await h.EditAsync(async db => (await db.RequestForms.SingleAsync()).OrganizationId = "other");
        using var denied = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "self-form", PayloadJson = "{}" });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("request_organization_mismatch", error.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("traceId").GetString()));
        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Requests.ToListAsync());
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
            (await db.RequestForms.SingleAsync()).OrganizationId = h.OrganizationId;
        });
        using var corrected = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "self-form", PayloadJson = "{}" });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_account_customer_organization_and_permission_revocation_all_deny_new_work(bool postgres)
    {
        await using var h = await CreateAsync(postgres, false);
        var submission = new SubmitSelfServiceRequestDto { SubmissionId = Guid.NewGuid(), RequestFormId = "self-form", PayloadJson = "{}" };
        using var created = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", submission);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        foreach (var condition in new[] { "account", "customer", "organization", "permission" })
        {
            await using (var scope = h.App.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                (await identity.Users.SingleAsync(x => x.Id == "owner")).IsEnabled = condition != "account";
                await identity.SaveChangesAsync();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var link = await db.CustomerAuthLinks.SingleAsync(x => x.LocalAccountId == "owner");
                (await db.Customers.SingleAsync(x => x.Id == link.CustomerId)).IsEnabled = condition != "customer";
                (await db.Organizations.SingleAsync(x => x.Id == h.OrganizationId)).IsEnabled = condition != "organization";
                if (condition == "permission") db.ScopedRoleAssignments.RemoveRange(await db.ScopedRoleAssignments.Where(x => x.UserId == "owner").ToListAsync());
                await db.SaveChangesAsync();
            }
            using var denied = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
                new SubmitSelfServiceRequestDto { RequestFormId = "self-form", PayloadJson = "{}" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(await denied.Content.ReadAsStringAsync()));
            using var replay = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", submission);
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        }
        await h.EditAsync(async db => Assert.Single(await db.Requests.IgnoreQueryFilters().ToListAsync()));
    }

    private static async Task<IncidentReceiverTests.Harness> CreateAsync(bool postgres, bool administrator = true)
    {
        var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
                services.AddAuthorization(options => options.AddPolicy("SelfService.User", policy => policy.RequireAuthenticatedUser()));
            }, configureApp: app => app.MapSelfServiceRequestEndpoints());
        await h.EditAsync(async db =>
        {
            db.Users.Add(new User { Id = "owner", Name = "Synthetic local owner", Email = "owner@example.test", OrganizationId = h.OrganizationId });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "owner", OrganizationId = h.OrganizationId, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.RequestForms.Add(new RequestForm
            {
                Id = "self-form", Title = "Synthetic self request", OrganizationId = h.OrganizationId,
                ReleaseStatus = RequestFormReleaseStatus.Production,
                JsonSchema = JsonDocument.Parse("""{"title":"Synthetic self request","fields":[],"tasks":[]}""")
            });
            await Task.CompletedTask;
        });
        if (!administrator)
        {
            await using var scope = h.App.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.Users.SingleAsync(x => x.Id == "owner")).IsInstanceAdministrator = false;
            await identity.SaveChangesAsync();
        }
        return h;
    }
}
