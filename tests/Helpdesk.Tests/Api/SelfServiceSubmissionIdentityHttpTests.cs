using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Services;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Pairing;
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
            Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.False(string.IsNullOrWhiteSpace(await denied.Content.ReadAsStringAsync()));
            using var replay = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", submission);
            Assert.Contains(replay.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        await h.EditAsync(async db => Assert.Single(await db.Requests.IgnoreQueryFilters().ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_grants_cover_a_shared_path_secondary_requester_and_owned_history(bool postgres)
    {
        await using var h = await CreateAsync(postgres, administrator: false);
        var a = h.OrganizationId;
        const string b = "organization-b";
        const string c = "organization-c";
        const string disabled = "organization-disabled";
        await h.EditAsync(async db =>
        {
            db.Organizations.AddRange(new Organization { Id = b, Name = "Organization B" },
                new Organization { Id = c, Name = "Organization C" },
                new Organization { Id = disabled, Name = "Disabled", IsEnabled = false });
            db.ScopedRoleAssignments.AddRange(
                new ScopedRoleAssignment { UserId = "owner", OrganizationId = b, RoleKey = ScopedRoleCatalog.SelfServiceUser },
                new ScopedRoleAssignment { UserId = "owner", OrganizationId = c, RoleKey = ScopedRoleCatalog.IncidentReader },
                new ScopedRoleAssignment { UserId = "owner", OrganizationId = disabled, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Customers.Add(new Customer { Id = "unrelated-b", Name = "Unrelated customer", Email = "owner@example.test", OrganizationId = b });
            db.Services.AddRange(
                new Service { Id = "starter", Name = "New starter", AllowedOrganizationIds = [a, b, c, disabled] },
                new Service { Id = "general", Name = "General", AllowedOrganizationIds = [a, b] },
                new Service { Id = "folder-a", Name = "Organization A only", AllowedOrganizationIds = [a] },
                new Service { Id = "below-a", Name = "Shared leaf", ParentServiceId = "folder-a", AllowedOrganizationIds = [a, b] },
                new Service { Id = "unassigned", Name = "Unassigned", AllowedOrganizationIds = [] },
                new Service { Id = "cycle-1", Name = "Cycle", ParentServiceId = "cycle-2", AllowedOrganizationIds = [a] },
                new Service { Id = "cycle-2", Name = "Cycle", ParentServiceId = "cycle-1", AllowedOrganizationIds = [a] });
            var formA = await db.RequestForms.SingleAsync();
            formA.ServiceId = "starter";
            formA.Title = "Starter A";
            db.RequestForms.AddRange(
                Form("starter-b", "Starter B", "starter", b, [b]),
                Form("starter-c", "Starter C", "starter", c, [c]),
                Form("starter-disabled", "Starter disabled", "starter", disabled, [disabled]),
                Form("starter-unassigned", "Starter unassigned", "starter", a, []),
                Form("no-common-path", "No common organization", "below-a", b, [b]),
                Form("private-form", "Private form", "unassigned", a, [a]),
                Form("cycle-form", "Cyclic path", "cycle-1", a, [a]),
                Form("multi", "Common request", "general", null, [a, b]));
            db.RequestForms.Add(Form("test-form", "Testing only", "starter", a, [a], RequestFormReleaseStatus.InTesting));
        });

        using var root = await h.AdminAsync(HttpMethod.Get, "/api/v1/service-items");
        var folders = (await root.Content.ReadFromJsonAsync<List<ServiceItemDto>>())!;
        Assert.Equal(2, Assert.Single(folders, item => item.Id == "starter").AvailableRequestCount);
        Assert.DoesNotContain(folders, item => item.Id is "unassigned" or "cycle-1" or "cycle-2");
        using var starter = await h.AdminAsync(HttpMethod.Get, "/api/v1/service-items/starter");
        var starterItems = (await starter.Content.ReadFromJsonAsync<List<ServiceItemDto>>())!;
        Assert.Equal(new[] { "self-form", "starter-b" }, starterItems.Select(item => item.Id).Order());
        Assert.All(starterItems, item => Assert.Null(item.AllowedOrganizationIds));

        using var search = await h.AdminAsync(HttpMethod.Get, "/api/v1/service-items/search?q=starter&pageSize=1");
        var results = (await search.Content.ReadFromJsonAsync<PagedResponse<ServiceItemDto>>())!;
        Assert.Equal(3, results.TotalCount);
        Assert.Single(results.Items);

        foreach (var id in new[] { "starter-c", "starter-disabled", "starter-unassigned", "no-common-path", "private-form", "cycle-form", "test-form" })
        {
            using var direct = await h.AdminAsync(HttpMethod.Get, $"/api/v1/request-forms/{id}");
            using var context = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/submission-context?requestFormId={id}");
            using var post = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", new SubmitSelfServiceRequestDto { RequestFormId = id });
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, context.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
        using var deniedFolder = await h.AdminAsync(HttpMethod.Get, "/api/v1/services/unassigned/breadcrumb");
        Assert.Equal(HttpStatusCode.NotFound, deniedFolder.StatusCode);

        using var contextB = await h.AdminAsync(HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=starter-b");
        var eligibleB = (await contextB.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>())!;
        Assert.True(eligibleB.CanSubmit);
        Assert.Equal(b, Assert.Single(eligibleB.EligibleOrganizations).Id);
        using var multiContext = await h.AdminAsync(HttpMethod.Get, "/api/v1/self-service/submission-context?requestFormId=multi");
        var options = (await multiContext.Content.ReadFromJsonAsync<SelfServiceSubmissionContextDto>())!;
        Assert.Equal(new[] { a, b }.Order(), options.EligibleOrganizations.Select(org => org.Id).Order());
        Assert.Null(options.OrganizationId);
        Assert.False(options.CanSubmit);
        using var absent = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "multi" });
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
        using var wrong = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "starter-b", TargetOrganizationId = a });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using var forged = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "starter-b", TargetOrganizationId = b,
                RequestedForPersonId = h.CustomerId, RequestedForPersonSource = SelfServiceRequestPersonSource.Customer });
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);

        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Requests.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RequestTasks.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
        });

        var dtoB = new SubmitSelfServiceRequestDto { RequestFormId = "starter-b", TargetOrganizationId = b, SubmissionId = Guid.NewGuid() };
        var receiptsB = await Task.WhenAll(SubmitAsync(h, dtoB), SubmitAsync(h, dtoB));
        Assert.Equal(receiptsB[0].RequestId, receiptsB[1].RequestId);
        var receiptB = receiptsB[0];
        Assert.Equal(b, receiptB.OrganizationId);
        Assert.Equal("Organization B", receiptB.OrganizationName);
        await h.EditAsync(async db => Assert.Empty(await db.CustomerAuthLinks.ToListAsync()));

        var receiptA = await SubmitAsync(h, new SubmitSelfServiceRequestDto { RequestFormId = "self-form", SubmissionId = Guid.NewGuid() });
        Assert.Equal(a, receiptA.OrganizationId);
        var shared = new SubmitSelfServiceRequestDto { RequestFormId = "multi", TargetOrganizationId = a, SubmissionId = Guid.NewGuid() };
        var sharedReceipt = await SubmitAsync(h, shared);
        shared.TargetOrganizationId = b;
        using var changed = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", shared);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        await h.EditAsync(async db =>
        {
            var profiles = await db.Customers.Where(customer => customer.IdentityUserId == "owner").ToListAsync();
            Assert.Equal(new[] { a, b }.Order(), profiles.Select(customer => customer.OrganizationId).Order());
            var link = Assert.Single(await db.CustomerAuthLinks.ToListAsync());
            Assert.Equal(profiles.Single(customer => customer.OrganizationId == a).Id, link.CustomerId);
            Assert.Equal("owner", link.LocalAccountId);
            Assert.Equal(a, (await db.Users.SingleAsync(user => user.Id == "owner")).OrganizationId);
            var requests = await db.Requests.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(3, requests.Count);
            foreach (var request in requests)
                Assert.Equal(profiles.Single(customer => customer.OrganizationId == request.OrganizationId).Id, request.CustomerId);
            var tasks = await db.RequestTasks.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, tasks.Count);
            var secondary = Assert.Single(tasks, task => task.OrganizationId == b);
            Assert.Equal(receiptB.RequestId, secondary.RequestId);
            Assert.Equal(profiles.Single(customer => customer.OrganizationId == b).Id, secondary.CustomerId);
            (await db.RequestForms.IgnoreQueryFilters().SingleAsync(form => form.Id == "starter-b")).AllowedOrganizationIds = [];
        });

        using var history = await h.AdminAsync(HttpMethod.Get, "/api/v1/self-service/requests");
        var owned = (await history.Content.ReadFromJsonAsync<PagedResponse<MyRequestListItemDto>>())!;
        Assert.Equal(new[] { receiptA.RequestId, receiptB.RequestId, sharedReceipt.RequestId }.Order(), owned.Items.Select(item => item.Id).Order());
        using var detail = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/requests/{receiptB.RequestId}");
        Assert.Equal(b, (await detail.Content.ReadFromJsonAsync<MyRequestDetailDto>())!.OrganizationId);
        using var taskResponse = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/requests/{receiptB.RequestId}/tasks");
        Assert.Equal("Review request", Assert.Single((await taskResponse.Content.ReadFromJsonAsync<List<MyRequestTaskDto>>())!).Name);
        using var unpublished = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", dtoB);
        Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);

        await h.EditAsync(async db =>
        {
            db.ScopedRoleAssignments.Remove(await db.ScopedRoleAssignments.SingleAsync(grant => grant.OrganizationId == b));
        });
        using var revokedDetail = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/requests/{receiptB.RequestId}");
        using var revokedTasks = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/requests/{receiptB.RequestId}/tasks");
        using var revokedStream = await h.AdminAsync(HttpMethod.Get, $"/api/v1/self-service/requests/{receiptB.RequestId}/timeline/stream");
        Assert.Equal(HttpStatusCode.NotFound, revokedDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedTasks.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedStream.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_dataset_and_saved_connection_cannot_admit_work(bool postgres)
    {
        await using var h = await CreateAsync(postgres, administrator: false);
        var org = h.OrganizationId;
        var connectionId = Guid.NewGuid();
        await h.EditAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = "org-b", Name = "Other organization" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-b", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.DatasetDefinitions.AddRange(
                new DatasetDefinition { Id = "dataset-a", OrganizationId = org, Name = "Dataset A", KeyColumn = "id", DisplayColumn = "label" },
                new DatasetDefinition { Id = "dataset-b", OrganizationId = "org-b", Name = "Dataset B", KeyColumn = "id", DisplayColumn = "label" });
            db.DatasetRows.Add(new DatasetRow { DatasetId = "dataset-b", OrganizationId = "org-b", ExternalKey = "b-only", DataJson = JsonDocument.Parse("""{"id":"b-only","label":"Foreign"}""") });
            var local = await db.RequestForms.SingleAsync();
            local.JsonSchema = JsonDocument.Parse("""{"title":"Bound choice","fields":[{"key":"department","label":"Department","type":"text","dataBinding":{"datasetId":"dataset-a","allowFreeText":false}}]}""");
            var foreignDataset = Form("foreign-dataset", "Foreign dataset", "", org, [org]);
            foreignDataset.JsonSchema = JsonDocument.Parse("""{"fields":[{"key":"department","label":"Department","type":"text","dataBinding":{"datasetId":"dataset-b"}}]}""");
            db.RequestForms.AddRange(foreignDataset, Form("foreign-connection", "Foreign connection", "", org, [org]));
            db.Set<SystemConnection>().Add(new SystemConnection
            {
                Id = connectionId, State = "connected", PairId = "test-pair",
                MappingJson = JsonSerializer.Serialize(new PairingMapping("connection-b", "test-pair", "B", "peer-b", "org-b", null, false, true),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            db.AutomationBindings.Add(new AutomationBinding
            {
                RequestFormId = "foreign-connection", OrganizationId = org, SystemConnectionId = connectionId,
                Enabled = true, TaskTemplateId = Guid.NewGuid(), OrchestrationRequestDefinitionId = "peer-b-job"
            });
        });
        foreach (var id in new[] { "foreign-dataset", "foreign-connection" })
        {
            using var denied = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
                new SubmitSelfServiceRequestDto { RequestFormId = id, TargetOrganizationId = org });
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using var foreignOption = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests",
            new SubmitSelfServiceRequestDto { RequestFormId = "self-form", TargetOrganizationId = org, PayloadJson = """{"department":"b-only"}""" });
        Assert.Equal(HttpStatusCode.BadRequest, foreignOption.StatusCode);
        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Requests.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RequestTasks.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
        });
    }

    private static RequestForm Form(string id, string title, string folder, string? owner, List<string> audience,
        RequestFormReleaseStatus release = RequestFormReleaseStatus.Production) =>
        new()
        {
            Id = id, Title = title, ServiceId = folder, OrganizationId = owner,
            AllowedOrganizationIds = audience, ReleaseStatus = release,
            JsonSchema = JsonSerializer.SerializeToDocument(new
            {
                title,
                tasks = new[] { new { id = Guid.NewGuid(), name = "Review request", type = "manual", order = 1 } }
            })
        };

    private static async Task<SelfServiceRequestSubmittedDto> SubmitAsync(IncidentReceiverTests.Harness harness, SubmitSelfServiceRequestDto dto)
    {
        using var response = await harness.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", dto);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SelfServiceRequestSubmittedDto>())!;
    }

    private static async Task<IncidentReceiverTests.Harness> CreateAsync(bool postgres, bool administrator = true)
    {
        var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddScoped<SelfServiceAudienceService>();
                services.AddAuthorization(options => options.AddPolicy("SelfService.User", policy => policy.RequireAuthenticatedUser()));
            }, configureApp: app =>
            {
                app.MapServiceEndpoints();
                app.MapSelfServiceRequestEndpoints();
                app.MapSelfServiceMyRequestsEndpoints();
            });
        await h.EditAsync(async db =>
        {
            db.Users.Add(new User { Id = "owner", Name = "Synthetic local owner", Email = "owner@example.test", OrganizationId = h.OrganizationId });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "owner", OrganizationId = h.OrganizationId, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.RequestForms.Add(new RequestForm
            {
                Id = "self-form", Title = "Synthetic self request", OrganizationId = h.OrganizationId, AllowedOrganizationIds = [h.OrganizationId],
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
