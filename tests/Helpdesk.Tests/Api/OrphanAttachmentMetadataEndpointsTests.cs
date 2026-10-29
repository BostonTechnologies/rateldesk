using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class LocalAuthenticationEndpointsTests
{
    [Fact]
    public async Task Orphan_metadata_retirement_requires_instance_admin_and_preserves_other_records()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await users.CreateAsync(new ApplicationUser
        {
            UserName = "orphan-reader@example.test", Email = "orphan-reader@example.test"
        }, "correct horse battery staple")).Succeeded);
        var target = new Attachment { Id = Guid.NewGuid(), TicketId = "deleted-parent", FilePath = Guid.NewGuid().ToString("N") + ".txt" };
        var retained = new Attachment { Id = Guid.NewGuid(), TicketId = "another-parent", FilePath = Guid.NewGuid().ToString("N") + ".txt" };
        db.Attachments.AddRange(target, retained);
        await db.SaveChangesAsync();
        var path = $"/api/v1/attachments/{target.Id}/orphan-metadata";
        using var anonymous = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path)).StatusCode);
        using var reader = CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("orphan-reader@example.test", "correct horse battery staple"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(path)).StatusCode);
        Assert.True(await db.Attachments.AnyAsync(x => x.Id == target.Id));
        using var admin = CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(path)).StatusCode);
        Assert.False(await db.Attachments.AnyAsync(x => x.Id == target.Id));
        Assert.True(await db.Attachments.AnyAsync(x => x.Id == retained.Id));
    }

    [Fact]
    public async Task Orphan_metadata_retirement_refuses_existing_parent_and_private_bytes()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<TicketAttachmentFileStore>();
        var parent = new Incident { Id = "retained-attachment-parent", OrganizationId = "org-one", Title = "Retained" };
        var attached = new Attachment { Id = Guid.NewGuid(), TicketId = parent.Id, FilePath = Guid.NewGuid().ToString("N") + ".txt" };
        var withBytes = new Attachment { Id = Guid.NewGuid(), TicketId = "absent-byte-parent", FilePath = Guid.NewGuid().ToString("N") + ".txt" };
        db.Incidents.Add(parent); db.Attachments.AddRange(attached, withBytes); await db.SaveChangesAsync();
        var file = store.GetWritePath(withBytes.FilePath);
        await File.WriteAllTextAsync(file, "owned private fixture");
        try
        {
            using var admin = CreateClient();
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/local-auth/login",
                new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/attachments/{attached.Id}/orphan-metadata")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/attachments/{withBytes.Id}/orphan-metadata")).StatusCode);
            Assert.Equal("owned private fixture", await File.ReadAllTextAsync(file));
            Assert.Equal(2, await db.Attachments.CountAsync());
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("")]
    public async Task Orphan_metadata_retirement_refuses_unsafe_storage_names(string storedName)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var row = new Attachment { Id = Guid.NewGuid(), TicketId = "absent-parent", FilePath = storedName };
        db.Attachments.Add(row); await db.SaveChangesAsync();
        using var admin = CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/attachments/{row.Id}/orphan-metadata")).StatusCode);
        Assert.True(await db.Attachments.AnyAsync(x => x.Id == row.Id));
    }
}
