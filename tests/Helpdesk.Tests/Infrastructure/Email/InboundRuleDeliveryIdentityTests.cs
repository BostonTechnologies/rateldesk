using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class InboundRuleDeliveryIdentityTests
{
    [Fact]
    public async Task Successful_non_stopping_rule_remains_handled_after_subsequent_unmatched_rules()
    {
        await using var db = CreateDb();
        db.InboundEmailRules.Add(Rule("create", 1));
        db.InboundEmailRules.Add(Rule("later", 2));
        await db.SaveChangesAsync();
        var incident = new Incident { Id = "once" };
        var executor = Substitute.For<IInboundEmailActionExecutor>();
        executor.ExecuteAsync(Arg.Any<InboundEmailRule>(), Arg.Any<InboundEmailRuleActionConfig>(),
            Arg.Any<InboundEmailContext>(), Arg.Any<ForwardedEmailParseResult?>(), Arg.Any<CancellationToken>())
            .Returns(call => new InboundEmailRuleProcessingResult(call.Arg<InboundEmailRule>().Id == "create", false,
                call.Arg<InboundEmailRule>().Id == "create" ? incident : null));
        var processor = new InboundEmailRuleProcessor(db, Substitute.For<IForwardedEmailParser>(), executor,
            NullLogger<InboundEmailRuleProcessor>.Instance);
        var result = await processor.ProcessAsync(Context(Guid.NewGuid(), "receipt-a"));
        Assert.True(result.Handled);
        Assert.Same(incident, result.Ticket);
        await executor.Received(2).ExecuteAsync(Arg.Any<InboundEmailRule>(), Arg.Any<InboundEmailRuleActionConfig>(),
            Arg.Any<InboundEmailContext>(), Arg.Any<ForwardedEmailParseResult?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reused_rfc_message_id_does_not_suppress_distinct_native_delivery()
    {
        await using var db = CreateDb();
        var mailboxId = Guid.NewGuid();
        var rule = Rule("create", 1);
        rule.ConditionsJson = """[{"type":5}]""";
        db.InboundEmailRules.Add(rule);
        foreach (var key in new[] { "receipt-a", "reused-rfc-id" })
            db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
            {
                MessageId = key, MailboxKey = mailboxId.ToString("D"), RuleId = rule.Id,
                Status = InboundEmailProcessingStatus.Succeeded
            });
        await db.SaveChangesAsync();
        var executor = Substitute.For<IInboundEmailActionExecutor>();
        executor.ExecuteAsync(Arg.Any<InboundEmailRule>(), Arg.Any<InboundEmailRuleActionConfig>(),
            Arg.Any<InboundEmailContext>(), Arg.Any<ForwardedEmailParseResult?>(), Arg.Any<CancellationToken>())
            .Returns(new InboundEmailRuleProcessingResult(true, false, new Incident { Id = "second-delivery" }));
        var processor = new InboundEmailRuleProcessor(db, Substitute.For<IForwardedEmailParser>(), executor,
            NullLogger<InboundEmailRuleProcessor>.Instance);
        Assert.True((await processor.ProcessAsync(Context(mailboxId, "receipt-b"))).Handled);
        Assert.False((await processor.ProcessAsync(Context(mailboxId, "receipt-a"))).Handled);
        await executor.Received(1).ExecuteAsync(Arg.Any<InboundEmailRule>(), Arg.Any<InboundEmailRuleActionConfig>(),
            Arg.Any<InboundEmailContext>(), Arg.Any<ForwardedEmailParseResult?>(), Arg.Any<CancellationToken>());
    }

    private static InboundEmailRule Rule(string id, int priority) => new()
    {
        Id = id, Name = id, Enabled = true, StopProcessing = false, Priority = priority,
        ScopeType = InboundEmailRuleScopeType.Global, ConditionsJson = "[]", ActionsJson = """[{"type":0}]"""
    };

    private static InboundEmailContext Context(Guid mailboxId, string sourceKey) => new("reused-rfc-id", null,
        mailboxId, null, "support@example.com", "requester@example.com", "Requester", [], [], "Subject", "Body", "Body",
        DateTimeOffset.UtcNow, new Dictionary<string, string>(), []) { SourceMessageKey = sourceKey };

    private static HelpdeskDbContext CreateDb() => new(new DbContextOptionsBuilder<HelpdeskDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
}
