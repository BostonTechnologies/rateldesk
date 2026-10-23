extern alias NewWeb;
using Bunit;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using Timeline = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketTimeline;
using TimelineApiClient = NewWeb::HelpDesk.NewWeb.Services.TimelineApiClient;
using ITimelineApiClient = NewWeb::HelpDesk.NewWeb.Services.ITimelineApiClient;
using TimelineStreamService = NewWeb::HelpDesk.NewWeb.Services.TimelineStreamService;

namespace Helpdesk.Tests.Api;

public sealed partial class TimelineEffortPersistenceTests
{
    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task Rendered_timeline_keeps_live_fraction_after_reload_and_distinguishes_unknown_from_zero(string module)
    {
        await using var harness = await EffortHarness.CreateAsync();
        await harness.SeedTicketAsync(module);
        await using (var liveContext = TimelineContext(harness.Client))
        {
            var live = liveContext.Render<Timeline>(parameters => parameters
                .Add(component => component.TicketId, "ticket-effort")
                .Add(component => component.TicketType, module.ToLowerInvariant() + "s"));
            await harness.Bus.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, harness.Bus.SubscriptionCount);
            await harness.PostAsync(module, 0.25, true);
            live.WaitForAssertion(() =>
            {
                Assert.Contains("0.25h", live.Markup);
                Assert.Single(live.FindAll(".timeline-human-card"));
            });
        }

        await harness.PostAsync(module, 0.625, false);
        await harness.PostAsync(module, 0, true);
        await harness.WithDbAsync(async db =>
        {
            db.TicketTimelineEvents.Add(new TicketTimelineEvent
            {
                TicketId = "ticket-effort", EventType = TimelineEventType.InternalNote,
                CreatedByUserId = "technician-one", CreatedByUserName = "Technician",
                MessageText = "Legacy note with unavailable effort"
            });
            await db.SaveChangesAsync();
        });

        // Recreate the component and its clients, so every displayed value now
        // comes from the production GET endpoint in a fresh request scope.
        await using var reloadContext = TimelineContext(harness.Client);
        var reloaded = reloadContext.Render<Timeline>(parameters => parameters
            .Add(component => component.TicketId, "ticket-effort")
            .Add(component => component.TicketType, module.ToLowerInvariant() + "s"));
        reloaded.WaitForAssertion(() =>
        {
            Assert.Contains("0.25h", reloaded.Markup);
            Assert.Contains("0.625h", reloaded.Markup);
            Assert.Contains(reloaded.FindAll(".mud-chip-content"), chip => chip.TextContent.Trim() == "0h");
            Assert.Contains("Effort unavailable", reloaded.Markup);
            Assert.Equal(4, reloaded.FindAll(".timeline-human-card").Count);
        });
    }

    private static BunitContext TimelineContext(HttpClient client)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        context.Services.AddSingleton(factory);
        context.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        context.Services.AddSingleton<ITimelineApiClient, TimelineApiClient>();
        context.Services.AddSingleton(new TimelineStreamService(factory, NullLogger<TimelineStreamService>.Instance));
        return context;
    }
}
