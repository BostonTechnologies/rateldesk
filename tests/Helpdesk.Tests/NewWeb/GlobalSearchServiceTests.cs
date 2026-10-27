extern alias NewWeb;

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using NewWeb::HelpDesk.NewWeb.Services.Search;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Tests.NewWeb;

public sealed class GlobalSearchServiceTests
{
    [Theory]
    [InlineData("Incident.Read", "incidents")]
    [InlineData("Incident.Write", "incidents")]
    [InlineData("Request.Read", "requests")]
    [InlineData("Change.Read", "changes")]
    public async Task Scoped_module_roles_discover_only_their_permitted_ticket_sections(string permission, string section)
    {
        var service = CreateService(new FakeHttpClientFactory(new Dictionary<string, object?>()), permission);
        var groups = await service.SearchAsync("");
        Assert.Contains(groups, group => group.Key == section);
        Assert.DoesNotContain(groups, group => group.Key == "team");
        foreach (var otherSection in new[] { "incidents", "requests", "changes" }.Where(key => key != section))
            Assert.DoesNotContain(groups, group => group.Key == otherSection);
    }

    [Fact]
    public async Task Investigation_search_preserves_query_and_only_exposes_the_writer_section()
    {
        var id = Guid.NewGuid();
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/ai/ops/investigations?q=router"] = Enumerable.Range(0, 6)
                .Select(i => new Helpdesk.Shared.DTOs.AiOps.AiInvestigationDto(i == 0 ? id : Guid.NewGuid(), "a", $"Router {i}", "ticket", "incidents", "", DateTime.UtcNow)).ToList()
        });
        var group = (await CreateService(factory, "Incident.Write").SearchAsync("router")).Single(x => x.Key == "investigations");
        Assert.Equal(5, group.Results.Count);
        Assert.Equal($"/ai/ops?id={id}", group.Results[0].Href);
        Assert.Equal("/ai/ops?q=router", group.ContinueHref);
        Assert.DoesNotContain(await CreateService(factory, "Incident.Read").SearchAsync(""), x => x.Key == "investigations");
    }

    [Fact]
    public async Task Empty_query_shows_role_appropriate_sections()
    {
        var service = CreateService(new FakeHttpClientFactory(new Dictionary<string, object?>()), "Technician");

        var groups = await service.SearchAsync("");

        Assert.Contains(groups, x => x.Key == "self-service");
        Assert.Contains(groups, x => x.Key == "incidents");
        Assert.DoesNotContain(groups, x => x.Key == "team");
    }

    [Fact]
    public async Task Admin_query_includes_admin_sections()
    {
        var service = CreateService(new FakeHttpClientFactory(new Dictionary<string, object?>()), "HelpdeskAdmin");

        var groups = await service.SearchAsync("");

        Assert.Contains(groups, x => x.Key == "team");
        Assert.Contains(groups, x => x.Key == "customers");
        Assert.Contains(groups, x => x.Key == "organizations");
    }

    [Fact]
    public async Task Search_caps_results_and_adds_continue_link()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/service-items/search?q=vpn&pageSize=6&includeTotal=false"] = new PagedResponse<ServiceItemDto>
            {
                TotalCount = 6,
                Items = Enumerable.Range(1, 6)
                    .Select(i => new ServiceItemDto
                    {
                        Id = $"form-{i}",
                        Name = $"VPN request {i}",
                        Description = "Access",
                        ItemType = ServiceItemType.RequestForm
                    })
                    .ToList()
            }
        });
        var service = CreateService(factory, "HelpdeskAdmin");

        var selfService = (await service.SearchAsync("vpn")).Single(x => x.Key == "self-service");

        Assert.Equal(5, selfService.Results.Count);
        Assert.Equal("/self-service?search=vpn", selfService.ContinueHref);
        Assert.Equal("/self-service/request/form-1", selfService.Results[0].Href);
    }

    [Fact]
    public async Task Ticket_search_uses_active_only_endpoints()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/incidents?page=1&pageSize=6&activeOnly=true&includeTotal=false&summaryOnly=true&q=router"] = new PagedResponse<IncidentDto>(),
            ["/api/v1/requests?page=1&pageSize=6&activeOnly=true&includeTotal=false&summaryOnly=true&q=router"] = new PagedResponse<RequestDto>(),
            ["/api/v1/changes?page=1&pageSize=6&activeOnly=true&includeTotal=false&summaryOnly=true&q=router"] = new PagedResponse<ChangeDto>()
        });
        var service = CreateService(factory, "HelpdeskAdmin");

        await service.SearchAsync("router");

        Assert.Contains(factory.RequestedPaths, x => x.Contains("/api/v1/incidents") && x.Contains("activeOnly=true"));
        Assert.Contains(factory.RequestedPaths, x => x.Contains("/api/v1/requests") && x.Contains("activeOnly=true"));
        Assert.Contains(factory.RequestedPaths, x => x.Contains("/api/v1/changes") && x.Contains("activeOnly=true"));
        Assert.Contains(factory.RequestedPaths, x => x.Contains("/api/v1/incidents") && x.Contains("includeTotal=false"));
        Assert.Contains(factory.RequestedPaths, x => x.Contains("/api/v1/incidents") && x.Contains("summaryOnly=true"));
    }

    [Fact]
    public async Task Change_search_renders_lifecycle_state_metadata()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/changes?page=1&pageSize=6&activeOnly=true&includeTotal=false&summaryOnly=true&q=firewall"] = new PagedResponse<ChangeDto>
            {
                Items =
                [
                    new ChangeDto
                    {
                        Id = "change-1",
                        TrackingId = "CHG-FIREWALL",
                        Title = "Firewall update",
                        State = TicketState.Replied,
                        LifecycleState = ChangeLifecycleState.ImplementationInProgress,
                        ChangeType = "Standard"
                    }
                ]
            }
        });
        var service = CreateService(factory, "HelpdeskAdmin");

        var changes = (await service.SearchAsync("firewall")).Single(x => x.Key == "changes");

        var result = Assert.Single(changes.Results);
        Assert.Contains("Implementation In Progress", result.Meta);
        Assert.DoesNotContain("Replied", result.Meta);
    }

    [Fact]
    public async Task Admin_search_uses_bounded_lookup_endpoints()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/global-search/users?q=jere&pageSize=6"] = new PagedResponse<UserDto>
            {
                Items = [new UserDto("user-1", "Example User", "user@example.com", "HelpdeskAdmin", false)]
            },
            ["/api/v1/global-search/customers?q=jere&pageSize=6"] = new PagedResponse<CustomerDto>
            {
                Items =
                [
                    new CustomerDto
                    {
                        Id = "customer-1",
                        Name = "Example User",
                        Email = "user@example.com"
                    }
                ]
            }
        });
        var service = CreateService(factory, "HelpdeskAdmin");

        var groups = await service.SearchAsync("jere");

        Assert.Contains(factory.RequestedPaths, x => x == "/api/v1/global-search/users?q=jere&pageSize=6");
        Assert.Contains(factory.RequestedPaths, x => x == "/api/v1/global-search/customers?q=jere&pageSize=6");
        Assert.Contains(groups.Single(x => x.Key == "team").Results, x => x.Title == "Example User");
    }

    [Fact]
    public async Task Self_service_search_returns_linux_catalog_matches()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/service-items/search?q=linux&pageSize=6&includeTotal=false"] = new PagedResponse<ServiceItemDto>
            {
                Items =
                [
                    new ServiceItemDto
                    {
                        Id = "linux-disk",
                        Name = "Disk capacity report",
                        Description = "Disk capacity report",
                        ItemType = ServiceItemType.RequestForm
                    }
                ]
            }
        });
        var service = CreateService(factory, "HelpdeskAdmin");

        var selfService = (await service.SearchAsync("linux")).Single(x => x.Key == "self-service");

        Assert.Contains(selfService.Results, x => x.Title == "Disk capacity report");
    }

    [Fact]
    public async Task Incremental_search_returns_fast_groups_before_slow_groups()
    {
        var factory = new FakeHttpClientFactory(
            new Dictionary<string, object?>
            {
                ["/api/v1/incidents?page=1&pageSize=6&activeOnly=true&includeTotal=false&summaryOnly=true&q=broke"] = new PagedResponse<IncidentDto>
                {
                    Items =
                    [
                        new IncidentDto
                        {
                            Id = "incident-1",
                            TrackingId = "INC-8K6-A53-5WN",
                            Subject = "A broken PC"
                        }
                    ]
                },
                ["/api/v1/service-items/search?q=broke&pageSize=6&includeTotal=false"] = new PagedResponse<ServiceItemDto>()
            },
            new Dictionary<string, TimeSpan>
            {
                ["/api/v1/service-items/search?q=broke&pageSize=6&includeTotal=false"] = TimeSpan.FromSeconds(5)
            });
        var service = CreateService(factory, TimeSpan.FromSeconds(10), "HelpdeskAdmin");

        GlobalSearchGroup? firstCompleted = null;
        await foreach (var group in service.SearchIncrementalAsync("broke"))
        {
            if (!group.IsLoading)
            {
                firstCompleted = group;
                break;
            }
        }

        Assert.NotNull(firstCompleted);
        Assert.Equal("incidents", firstCompleted.Key);
        Assert.Contains(firstCompleted.Results, x => x.Title == "INC-8K6-A53-5WN");
    }

    [Fact]
    public async Task Incremental_search_times_out_slow_groups_with_continue_link()
    {
        var factory = new FakeHttpClientFactory(
            new Dictionary<string, object?>
            {
                ["/api/v1/service-items/search?q=linux&pageSize=6&includeTotal=false"] = new PagedResponse<ServiceItemDto>()
            },
            new Dictionary<string, TimeSpan>
            {
                ["/api/v1/service-items/search?q=linux&pageSize=6&includeTotal=false"] = TimeSpan.FromSeconds(5)
            });
        var service = CreateService(factory, TimeSpan.FromMilliseconds(50), "HelpdeskAdmin");

        GlobalSearchGroup? timedOut = null;
        await foreach (var group in service.SearchIncrementalAsync("linux"))
        {
            if (group.Key == "self-service" && group.TimedOut)
            {
                timedOut = group;
                break;
            }
        }

        Assert.NotNull(timedOut);
        Assert.False(timedOut.IsLoading);
        Assert.Equal("/self-service?search=linux", timedOut.ContinueHref);
    }

    private static GlobalSearchService CreateService(FakeHttpClientFactory factory, params string[] roles)
        => CreateService(factory, TimeSpan.FromSeconds(10), roles);

    private static GlobalSearchService CreateService(FakeHttpClientFactory factory, TimeSpan timeout, params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToList();
        claims.Add(new Claim(ClaimTypes.Name, "tester@example.com"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return new GlobalSearchService(factory, new FakeAuthenticationStateProvider(user), NullLogger<GlobalSearchService>.Instance, timeout);
    }

    private sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal _user;

        public FakeAuthenticationStateProvider(ClaimsPrincipal user)
        {
            _user = user;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(_user));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, object?> _responses;
        private readonly IReadOnlyDictionary<string, TimeSpan> _delays;
        private readonly List<string> _requestedPaths = new();

        public FakeHttpClientFactory(
            IReadOnlyDictionary<string, object?> responses,
            IReadOnlyDictionary<string, TimeSpan>? delays = null)
        {
            _responses = responses;
            _delays = delays ?? new Dictionary<string, TimeSpan>();
        }

        public IReadOnlyList<string> RequestedPaths => _requestedPaths;

        public HttpClient CreateClient(string name) =>
            new(new FakeHandler(_responses, _delays, _requestedPaths))
            {
                BaseAddress = new Uri("https://helpdesk.test")
            };
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, object?> _responses;
        private readonly IReadOnlyDictionary<string, TimeSpan> _delays;
        private readonly List<string> _requestedPaths;

        public FakeHandler(
            IReadOnlyDictionary<string, object?> responses,
            IReadOnlyDictionary<string, TimeSpan> delays,
            List<string> requestedPaths)
        {
            _responses = responses;
            _delays = delays;
            _requestedPaths = requestedPaths;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            _requestedPaths.Add(key);
            if (_delays.TryGetValue(key, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            var payload = _responses.TryGetValue(key, out var response)
                ? response
                : DefaultPayload(key);

            var json = JsonSerializer.Serialize(payload);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        private static object DefaultPayload(string key)
        {
            if (key.Contains("/api/v1/service-items/search", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<ServiceItemDto>();
            }

            if (key.Contains("/api/v1/incidents", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<IncidentDto>();
            }

            if (key.Contains("/api/v1/requests", StringComparison.OrdinalIgnoreCase) && !key.Contains("request-tasks", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<RequestDto>();
            }

            if (key.Contains("/api/v1/changes", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<ChangeDto>();
            }

            if (key.Contains("/api/v1/request-tasks", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<RequestTaskListItemDto>();
            }

            if (key.Contains("/api/v1/global-search/users", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<UserDto>();
            }

            if (key.Contains("/api/v1/global-search/customers", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<CustomerDto>();
            }

            if (key.Contains("/api/v1/global-search/organizations", StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResponse<OrganizationDto>();
            }

            return Array.Empty<object>();
        }
    }
}
