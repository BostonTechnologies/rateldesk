using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class NetclawLegacySessionConfirmationEndpointsTests
{
    [Fact]
    public async Task Null_conversation_ids_return_bad_request()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("test");
        builder.Services.AddSingleton(tenant);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<IIntegrationProviderSettingsService>(services => new IntegrationProviderSettingsService(
            services.GetRequiredService<HelpdeskDbContext>(),
            services.GetRequiredService<IConfiguration>(),
            new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
            Options.Create(new AiAssistantChatOptions()),
            new DatabaseOptions { Provider = "Sqlite" },
            TimeProvider.System,
            NullLogger<IntegrationProviderSettingsService>.Instance));
        builder.Services.AddSingleton<IAiAssistantChatClientFactory>(Substitute.For<IAiAssistantChatClientFactory>());
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAssertion(_ => true)));

        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapNetclawConnectivityEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var content = new StringContent(
            """{"historicalInstance":"dev","historicalEndpoint":"https://provider-a.example.test/hub/session","expectedEligibleConversations":1,"conversationIds":null}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/v1/admin/netclaw/legacy-sessions/confirm-owner", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_legacy_provider", body.GetProperty("code").GetString());
    }
}
