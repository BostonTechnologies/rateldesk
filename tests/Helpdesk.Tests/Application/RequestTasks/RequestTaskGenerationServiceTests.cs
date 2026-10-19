using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using NSubstitute;

namespace Helpdesk.Tests.Application.RequestTasks;

public sealed class RequestTaskGenerationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateForRequestAsync_PreservesStartPolicyAndUsesRuntimeDefaults(bool autoStart)
    {
        var requestForms = Substitute.For<IRepository<RequestForm>>();
        var requestTasks = Substitute.For<IRepository<RequestTask>>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        requestForms.GetAsync("form-1").Returns(new RequestForm
        {
            Id = "form-1",
            JsonSchema = JsonDocument.Parse($$"""
            {
              "fields": [],
              "tasks": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "name": "step1",
                  "order": 1,
                  "type": "automation",
                  "autoStart": {{autoStart.ToString().ToLowerInvariant()}}
                }
              ]
            }
            """)
        });

        RequestTask? created = null;
        requestTasks.CreateAsync(Arg.Do<RequestTask>(task => created = task))
            .Returns(call => call.Arg<RequestTask>());

        var service = new RequestTaskGenerationService(
            requestForms,
            requestTasks,
            new RequestFormSchemaParser(),
            domainEvents,
            correlation);

        var count = await service.GenerateForRequestAsync(new Request
        {
            Id = "request-1",
            RequestFormId = "form-1",
            OrganizationId = "tenant-1",
            CustomerId = "customer-1"
        }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.NotNull(created);
        Assert.Equal(RequestTaskType.Automation, created!.Type);
        Assert.Equal(autoStart, created.AutoStart);
        Assert.Equal(1800, created.ExpectedRuntimeSeconds);
        Assert.Equal(600, created.GraceSeconds);
        Assert.Equal(2400, created.HardTimeoutSeconds);
    }

    [Fact]
    public async Task GenerateForRequestAsync_ApprovalTemplate_CreatesApprovalTask()
    {
        var requestForms = Substitute.For<IRepository<RequestForm>>();
        var requestTasks = Substitute.For<IRepository<RequestTask>>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        requestForms.GetAsync("form-approval").Returns(new RequestForm
        {
            Id = "form-approval",
            JsonSchema = JsonDocument.Parse("""
            {
              "fields": [],
              "tasks": [
                {
                  "id": "33333333-3333-3333-3333-333333333333",
                  "name": "Approval phase",
                  "order": 1,
                  "type": "approval",
                  "approvalAllowedDays": 7,
                  "approvalApprovers": [
                    { "source": "Customer", "id": "cust-1", "name": "Approver", "email": "approver@example.com" }
                  ]
                }
              ]
            }
            """)
        });

        RequestTask? created = null;
        requestTasks.CreateAsync(Arg.Do<RequestTask>(task => created = task))
            .Returns(call => call.Arg<RequestTask>());

        var service = new RequestTaskGenerationService(
            requestForms,
            requestTasks,
            new RequestFormSchemaParser(),
            domainEvents,
            correlation);

        var count = await service.GenerateForRequestAsync(new Request
        {
            Id = "request-approval",
            RequestFormId = "form-approval",
            OrganizationId = "tenant-1",
            CustomerId = "customer-1"
        }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.NotNull(created);
        Assert.Equal(RequestTaskType.Approval, created!.Type);
        Assert.Equal(RequestTaskStatus.Pending, created.Status);
        Assert.Equal(TicketState.New, created.State);
        Assert.Null(created.ExpectedRuntimeSeconds);
        Assert.Null(created.HardTimeoutSeconds);
    }

    [Fact]
    public async Task GenerateForRequestAsync_EarlierApproval_BlocksLaterAutomation()
    {
        var requestForms = Substitute.For<IRepository<RequestForm>>();
        var requestTasks = Substitute.For<IRepository<RequestTask>>();
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.GetCorrelationId().Returns("corr-test");

        requestForms.GetAsync("form-gated").Returns(new RequestForm
        {
            Id = "form-gated",
            JsonSchema = JsonDocument.Parse("""
            {
              "fields": [],
              "tasks": [
                {
                  "id": "44444444-4444-4444-4444-444444444444",
                  "name": "Approval gate",
                  "order": 1,
                  "type": "approval",
                  "approvalAllowedDays": 7,
                  "approvalApprovers": [
                    { "source": "Customer", "id": "cust-1", "name": "Approver", "email": "approver@example.com" }
                  ]
                },
                {
                  "id": "55555555-5555-5555-5555-555555555555",
                  "name": "Run automation",
                  "order": 2,
                  "type": "automation",
                  "autoStart": true
                }
              ]
            }
            """)
        });

        var created = new List<RequestTask>();
        requestTasks.CreateAsync(Arg.Do<RequestTask>(task => created.Add(task)))
            .Returns(call => call.Arg<RequestTask>());

        var service = new RequestTaskGenerationService(
            requestForms,
            requestTasks,
            new RequestFormSchemaParser(),
            domainEvents,
            correlation);

        var count = await service.GenerateForRequestAsync(new Request
        {
            Id = "request-gated",
            RequestFormId = "form-gated",
            OrganizationId = "tenant-1",
            CustomerId = "customer-1"
        }, CancellationToken.None);

        Assert.Equal(2, count);
        var approval = Assert.Single(created, x => x.Type == RequestTaskType.Approval);
        var automation = Assert.Single(created, x => x.Type == RequestTaskType.Automation);
        Assert.False(approval.IsBlocked);
        Assert.True(automation.IsBlocked);
        await domainEvents.Received(1).PublishAsync(
            Arg.Is<RequestTaskBlockedEvent>(x => x.TaskId == automation.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetEffectiveDependencies_AddsEarlierApprovals_PreservesExplicitDependencies_WithoutDuplicates()
    {
        var approval1 = new RequestTaskTemplateModel
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Order = 1,
            Type = "approval"
        };
        var manual = new RequestTaskTemplateModel
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Order = 2,
            Type = "manual"
        };
        var approval2 = new RequestTaskTemplateModel
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Order = 3,
            Type = "approval"
        };
        var automation = new RequestTaskTemplateModel
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Order = 4,
            Type = "automation",
            DependsOn = [manual.Id, approval1.Id]
        };

        var dependencies = RequestTaskApprovalGate.GetEffectiveDependencies(
            automation,
            [approval1, manual, approval2, automation]);

        Assert.Equal([manual.Id, approval1.Id, approval2.Id], dependencies);
    }
}
