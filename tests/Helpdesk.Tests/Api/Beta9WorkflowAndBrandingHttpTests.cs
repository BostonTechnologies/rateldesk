using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Organization;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketServices = Helpdesk.Application.Services.Tickets;

namespace Helpdesk.Tests.Api;

public sealed class Beta9WorkflowAndBrandingHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Self_service_and_manual_completion_return_success_with_one_started_approval(bool postgres, bool manualPredecessor)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
                services.AddSingleton<TicketServices.ITicketRefGeneratorService, TicketServices.TicketRefGeneratorService>();
                services.AddAuthorization(options => options.AddPolicy("SelfService.User", p => p.RequireAuthenticatedUser()));
            }, configureApp: app => app.MapSelfServiceRequestEndpoints());
        var manualTemplate = Guid.NewGuid(); var approvalTemplate = Guid.NewGuid();
        var tasks = new List<object>();
        if (manualPredecessor) tasks.Add(new { id = manualTemplate, name = "Manual predecessor", type = "manual", order = 0 });
        tasks.Add(new { id = approvalTemplate, name = "Review", type = "approval", order = 1,
            dependsOn = manualPredecessor ? new[] { manualTemplate } : Array.Empty<Guid>(),
            approvalApprovers = new[] { new { source = "Customer", id = h.CustomerId, name = "Approver", email = "approver@example.test" } } });
        await h.EditAsync(async db =>
        {
            db.RequestForms.Add(new RequestForm { Id = "beta9-form", Title = "Approval fixture", OrganizationId = h.OrganizationId,
                JsonSchema = JsonSerializer.SerializeToDocument(new { title = "Approval fixture", fields = Array.Empty<object>(), tasks }) });
        });
        using var submitted = await h.AdminAsync(HttpMethod.Post, "/api/v1/self-service/requests", new SubmitSelfServiceRequestDto
        {
            RequestFormId = "beta9-form", PayloadJson = "{}", Title = "Synthetic beta.9 request",
            RequestedForPersonId = h.CustomerId, RequestedForPersonSource = SelfServiceRequestPersonSource.Customer
        });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var receipt = (await submitted.Content.ReadFromJsonAsync<SelfServiceRequestSubmittedDto>())!;
        if (manualPredecessor)
        {
            string? manualId = null;
            await h.EditAsync(async db =>
            {
                var manual = await db.RequestTasks.SingleAsync(x => x.RequestId == receipt.RequestId && x.Type == RequestTaskType.Manual);
                manualId = manual.Id;
                Assert.Empty(await db.RequestTaskApprovals.ToListAsync());
            });
            using var started = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{manualId}/start");
            started.EnsureSuccessStatusCode();
            using var completed = await h.AdminAsync(HttpMethod.Post, $"/api/v1/request-tasks/{manualId}/complete");
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        await h.EditAsync(async db =>
        {
            Assert.Single(await db.Requests.AsNoTracking().ToListAsync());
            var approval = await db.RequestTasks.AsNoTracking().SingleAsync(x => x.Type == RequestTaskType.Approval);
            Assert.Equal(RequestTaskStatus.PendingApproval, approval.Status);
            Assert.Single(await db.RequestTaskApprovals.AsNoTracking().Where(x => x.RequestTaskId == approval.Id).ToListAsync());
            Assert.Equal("PendingApproval", (await db.Requests.AsNoTracking().SingleAsync()).WorkflowStatus);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_branding_accepts_authenticated_multipart_and_serves_exact_signed_bytes(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services => services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", p => p.RequireRole("HelpdeskAdmin"))),
            configureApp: app => app.MapTenantBrandingEndpoints());
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("image/png");
        content.Add(file, "file", "synthetic-logo.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants/17/branding/logo") { Content = content };
        request.Headers.Add("X-Synthetic-Admin", "true");
        using var uploaded = await h.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var result = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var url = result.GetProperty("url").GetString()!;
        using var brandingResponse = await h.AdminAsync(HttpMethod.Get, "/api/v1/tenants/17/branding");
        Assert.Equal(url, (await brandingResponse.Content.ReadFromJsonAsync<TenantBranding>())!.LogoUrl);
        var path = new Uri(url).PathAndQuery.Replace("/prefix/api/", "/api/", StringComparison.Ordinal);
        using var image = await h.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());
        using var invalidToken = await h.Client.GetAsync(path + "tampered");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
    }
}
