extern alias NewWeb;
using Bunit;
using Helpdesk.Shared.DTOs.Incident;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using TicketCustomerPicker = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketCustomerPicker;
using TicketTable = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketTable<Helpdesk.Shared.DTOs.Incident.IncidentDto>;

namespace Helpdesk.Tests.NewWeb;

public sealed class TicketCustomerRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_cell_contains_both_rendering_paths_without_truncating_customer_values(bool custom)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory, UnusedClientFactory>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var name = "Example regional customer — " + new string('界', 180);
        var ticket = new IncidentDto { Id = "synthetic-row", TrackingId = "SYN-356", CustomerId = "synthetic-customer", CustomerName = name };
        var selected = new List<string>();
        var table = context.Render<TicketTable>(parameters =>
        {
            parameters.Add(value => value.TicketType, "incidents")
                .Add(value => value.OnRowClick, id => selected.Add(id))
                .Add(value => value.ServerData, (_, _) => Task.FromResult(new TableData<IncidentDto> { Items = [ticket], TotalItems = 1 }));
            if (custom)
            {
                parameters.Add(value => value.CustomerTemplate, item => builder =>
                {
                    builder.OpenComponent<TicketCustomerPicker>(0);
                    builder.AddAttribute(1, nameof(TicketCustomerPicker.ReadOnly), true);
                    builder.AddAttribute(2, nameof(TicketCustomerPicker.CustomerId), item.CustomerId);
                    builder.AddAttribute(3, nameof(TicketCustomerPicker.CustomerName), item.CustomerName);
                    builder.CloseComponent();
                });
            }
        });
        table.WaitForAssertion(() => Assert.Equal(name, table.Find("td[data-label=For] .ticket-for-content .mud-chip-content").TextContent.Trim()));
        Assert.Contains("ticket-for-column", table.Find("td[data-label=For]").ClassList);
        Assert.Contains("For", table.Find("th.ticket-for-column").TextContent);
        await table.Find("td[data-label='Tracking ID']").ClickAsync(new());
        Assert.Equal([ticket.Id], selected);
        Assert.Equal(name, ticket.CustomerName);
    }

    private sealed class UnusedClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("A rendered customer with a known identity must not load or modify customers.");
    }
}
