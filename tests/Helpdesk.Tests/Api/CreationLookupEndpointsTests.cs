using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.Api;

public sealed class CreationLookupEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unicode_case_insensitive_search_matches_names_and_emails_before_bounded_results(bool postgres)
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            for (var i = 0; i < 55; i++)
            {
                db.Organizations.Add(new Organization { Id = $"unicode-alpha-{i}", Name = $"Alpha {i:D2}" });
                db.Customers.Add(new Customer { Id = $"unicode-alpha-{i}", Name = $"Alpha {i:D2}", Email = $"alpha{i}@example.test", OrganizationId = "org" });
            }
            db.Organizations.Add(new Organization { Id = "unicode-org", Name = "MÜNCHEN 100%_\\" });
            db.Customers.Add(new Customer { Id = "unicode-contact", Name = "MÜNCHEN Customer", Email = "ÄNN@EXAMPLE.TEST", OrganizationId = "org" });
            db.Users.Add(new User { Id = "unicode-user", Name = "MÜNCHEN User", Email = "ÖPERATOR@EXAMPLE.TEST", OrganizationId = "org" });
            await db.SaveChangesAsync();
        });
        var initial = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request");
        Assert.DoesNotContain(initial!.Items, item => item.Id == "unicode-org");
        var organizations = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>(
            "/api/v1/ticketing/organizations/search?module=request&q=" + Uri.EscapeDataString("münchen 100%_\\"));
        Assert.Equal("unicode-org", Assert.Single(organizations!.Items).Id);
        var customers = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>(
            "/api/v1/ticketing/customers/search?module=request&organizationId=org&q=" + Uri.EscapeDataString("münchen"));
        Assert.Equal("unicode-contact", Assert.Single(customers!.Items).Id);
        var customerEmail = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>(
            "/api/v1/ticketing/customers/search?module=request&organizationId=org&q=" + Uri.EscapeDataString("änn@"));
        Assert.Equal("unicode-contact", Assert.Single(customerEmail!.Items).Id);
        var participants = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>(
            "/api/v1/ticketing/change-participants/search?organizationId=org&q=" + Uri.EscapeDataString("münchen"));
        Assert.Equal(new[] { "unicode-contact", "unicode-user" }, participants!.Items.Select(item => item.Id).Order());
        var participantEmail = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>(
            "/api/v1/ticketing/change-participants/search?organizationId=org&q=" + Uri.EscapeDataString("öperator@"));
        Assert.Equal("unicode-user", Assert.Single(participantEmail!.Items).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Change_user_without_management_authority_does_not_receive_creation_organizations_or_participants(bool postgres)
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create(postgres);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "change-user");
        var organizations = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=change");
        Assert.Empty(organizations!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/change-participants/search?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/change-participants/search?organizationId=other")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/customers/search?module=change&organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/organizations/org/change-participants")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organization_search_applies_search_before_limit_and_preserves_total_eligibility_and_legacy_lists(bool postgres)
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            for (var i = 0; i < 65; i++) db.Organizations.Add(new Organization { Id = $"search-org-{i}", Name = $"Alpha {i:D2}" });
            db.Organizations.AddRange(new Organization { Id = "late-org", Name = "Zulu Partner 100%_\\" },
                new Organization { Id = "disabled-org", Name = "Zulu Disabled", State = EntityState.Blocked });
            await db.SaveChangesAsync();
        });
        var first = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request");
        Assert.Equal(20, first!.Items.Count);
        Assert.True(first.HasMore);
        Assert.DoesNotContain(first.Items, x => x.Id == "late-org");
        var match = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request&q=pArTnEr");
        Assert.Equal("late-org", Assert.Single(match!.Items).Id);
        Assert.Equal(first.TotalEligible, match.TotalEligible);
        Assert.True(match.TotalEligible > 50);
        Assert.False(match.HasMore);
        var literal = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request&q=" + Uri.EscapeDataString("100%_\\"));
        Assert.Equal("late-org", Assert.Single(literal!.Items).Id);
        var hydrated = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request&id=late-org");
        Assert.Equal("Zulu Partner 100%_\\", Assert.Single(hydrated!.Items).Name);
        var bounded = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request&limit=100");
        Assert.Equal(50, bounded!.Items.Count);
        var legacy = await h.Client.GetFromJsonAsync<List<OrganizationDto>>("/api/v1/ticketing/organizations?module=request");
        Assert.Equal(first.TotalEligible, legacy!.Count);
        Assert.DoesNotContain(legacy, x => x.Id == "disabled-org");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_nonadmin_change_creator_can_search_users_and_enabled_contacts_but_not_support_or_foreign_people(bool postgres)
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            db.Customers.AddRange(new Customer { Id = "contact", Name = "Zulu Contact", Email = "contact@example.test", OrganizationId = "org" },
                new Customer { Id = "disabled", Name = "Zulu Disabled", Email = "disabled@example.test", OrganizationId = "org", State = EntityState.Blocked },
                new Customer { Id = "foreign", Name = "Zulu Foreign", Email = "foreign@example.test", OrganizationId = "other" },
                new Customer { Id = "shared-id", Name = "Own contact with a shared identifier", Email = "own-contact@example.test", OrganizationId = "org" });
            db.Users.Add(new User { Id = "shared-id", Name = "Unrelated foreign user", Email = "foreign-user@example.test", OrganizationId = "other" });
            await db.SaveChangesAsync();
        });
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "writer");
        var first = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>("/api/v1/ticketing/change-participants/search?organizationId=org");
        Assert.Equal(20, first!.Items.Count);
        Assert.True(first.HasMore);
        var found = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>("/api/v1/ticketing/change-participants/search?organizationId=org&q=zULu");
        Assert.Equal(new[] { "contact", "late" }, found!.Items.Select(x => x.Id).Order());
        Assert.Equal(first.TotalEligible, found.TotalEligible);
        var support = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>("/api/v1/ticketing/change-participants/search?organizationId=org&id=external");
        Assert.Empty(support!.Items);
        var collision = await h.Client.GetFromJsonAsync<CreationLookupResult<ChangeParticipantUserDto>>("/api/v1/ticketing/change-participants/search?organizationId=org&id=shared-id");
        var ownContact = Assert.Single(collision!.Items);
        Assert.Equal("Own contact with a shared identifier", ownContact.Name);
        Assert.Equal("Customer", ownContact.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/change-participants/search?organizationId=other")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/organizations/org/change-participants")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/ticketing/change-participants/search?organizationId=org&limit=0")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Customer_lookup_uses_complete_population_and_restricts_customer_actors_to_their_own_record(bool postgres)
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            for (var i = 0; i < 60; i++) db.Customers.Add(new Customer { Id = $"contact-{i}", Name = $"Alpha {i:D2}", Email = $"alpha{i}@example.test", OrganizationId = "org" });
            db.Customers.Add(new Customer { Id = "contact", Name = "Zulu Customer", Email = "own@example.test", OrganizationId = "org" });
            await db.SaveChangesAsync();
        });
        var first = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>("/api/v1/ticketing/customers/search?module=request&organizationId=org");
        Assert.Equal(20, first!.Items.Count);
        Assert.Equal(61, first.TotalEligible);
        var found = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>("/api/v1/ticketing/customers/search?module=request&organizationId=org&q=OWN@");
        Assert.Equal("contact", Assert.Single(found!.Items).Id);
        Assert.Equal(61, found.TotalEligible);
        var legacy = await h.Client.GetFromJsonAsync<List<CustomerDto>>("/api/v1/ticketing/customers?module=request&organizationId=org");
        Assert.Equal(61, legacy!.Count);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "own");
        var own = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>("/api/v1/ticketing/customers/search?module=request&organizationId=org");
        Assert.Equal("contact", Assert.Single(own!.Items).Id);
        Assert.Equal(1, own.TotalEligible);
        var deniedId = await h.Client.GetFromJsonAsync<CreationLookupResult<CustomerDto>>("/api/v1/ticketing/customers/search?module=request&organizationId=org&id=contact-0");
        Assert.Empty(deniedId!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/customers/search?module=request&organizationId=other")).StatusCode);
    }

    [Fact]
    public async Task Data_management_organization_search_uses_its_own_authority_without_ticket_module_grants()
    {
        await using var h = await AssigneeSearchEndpointsTests.Harness.Create();
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "data-manager");
        var permitted = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=data-management");
        Assert.Equal("org", Assert.Single(permitted!.Items).Id);
        var ticket = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=request");
        Assert.Empty(ticket!.Items);
        var foreign = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=data-management&id=other");
        Assert.Empty(foreign!.Items);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var denied = await h.Client.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>("/api/v1/ticketing/organizations/search?module=data-management");
        Assert.Empty(denied!.Items);
    }
}
