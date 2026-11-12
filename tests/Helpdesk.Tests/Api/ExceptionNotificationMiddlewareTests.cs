using System.Collections.Concurrent;
using System.Security.Claims;
using Helpdesk.API.Middleware;
using Helpdesk.Application.Notifications;
using Helpdesk.Infrastructure.Logging;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class ExceptionNotificationMiddlewareTests
{
    [Theory]
    [InlineData("/api/v1/notifications")]
    [InlineData("/api/v1/admin/directories/categories")]
    public async Task Client_abort_during_access_projection_does_not_publish_faults(string path)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipelineCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new BlockingAccessService(entered);
        await WithLoggerAsync(async (logger, notifications, recorded) =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.Run(async context =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "synthetic-user")], "Test"));
                var middleware = new ExceptionNotificationMiddleware(async request =>
                {
                    try
                    {
                        await new UserAccessClaimsMiddleware(_ => throw new InvalidOperationException("Access projection must not finish after abort."))
                            .InvokeAsync(request, access);
                    }
                    catch (OperationCanceledException) when (request.RequestAborted.IsCancellationRequested)
                    {
                        observedAbort.TrySetResult();
                        throw;
                    }
                }, logger);
                try { await middleware.InvokeAsync(context, notifications); }
                finally { pipelineCompleted.TrySetResult(); }
            });
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var cancellation = new CancellationTokenSource();
            var response = client.GetAsync(path, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
            await observedAbort.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pipelineCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(recorded);
        }, expectedFault: false);
    }

    [Theory]
    [InlineData("/api/v1/notifications")]
    [InlineData("/api/v1/admin/directories/categories")]
    [InlineData("/api/v1/tickets/stream")]
    public async Task Aborted_read_propagates_without_direct_or_logger_notifications(string path)
    {
        using var aborted = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Request.Path = path;
        context.Response.StatusCode = 202;
        await WithLoggerAsync(async (logger, notifications, recorded) =>
        {
            var middleware = new ExceptionNotificationMiddleware(async request =>
            {
                entered.SetResult();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = request.RequestAborted.Register(() => completion.TrySetCanceled(request.RequestAborted));
                await completion.Task;
            }, logger);
            var pending = middleware.InvokeAsync(context, notifications);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            aborted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(202, context.Response.StatusCode);
            Assert.Empty(recorded);
        }, expectedFault: false);
    }

    [Theory]
    [InlineData("unexpected_cancellation")]
    [InlineData("dependency_timeout")]
    [InlineData("request_timeout")]
    [InlineData("server_fault")]
    public async Task Genuine_faults_remain_observable_through_both_notification_paths(string cause)
    {
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/notifications";
        if (cause == "request_timeout")
        {
            context.RequestAborted = timeout.Token;
            context.Features.Set<IHttpRequestTimeoutFeature>(new TimeoutFeature(timeout.Token));
        }
        Exception failure = cause switch
        {
            "dependency_timeout" => new TimeoutException("Synthetic dependency timeout"),
            "server_fault" => new DbUpdateException("Synthetic database fault"),
            _ => new OperationCanceledException("Synthetic cancellation", timeout.Token)
        };
        await WithLoggerAsync(async (logger, notifications, recorded) =>
        {
            var middleware = new ExceptionNotificationMiddleware(_ => Task.FromException(failure), logger);
            var actual = await Assert.ThrowsAnyAsync<Exception>(() => middleware.InvokeAsync(context, notifications));
            Assert.Same(failure, actual);
            Assert.Contains(recorded, item => item.Severity == NotificationSeverity.Critical && item.Message.Contains(cause, StringComparison.Ordinal));
        }, expectedFault: true);
    }

    [Fact]
    public async Task Started_response_is_preserved_when_its_request_is_aborted()
    {
        using var aborted = new CancellationTokenSource(); aborted.Cancel();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { StatusCode = 202 });
        await WithLoggerAsync(async (logger, notifications, recorded) =>
        {
            var failure = new OperationCanceledException(aborted.Token);
            var middleware = new ExceptionNotificationMiddleware(_ => Task.FromException(failure), logger);
            Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context, notifications)));
            Assert.True(context.Response.HasStarted); Assert.Equal(202, context.Response.StatusCode); Assert.Empty(recorded);
        }, expectedFault: false);
    }

    [Fact]
    public async Task Ordinary_read_succeeds_without_notification()
    {
        await WithLoggerAsync(async (logger, notifications, recorded) =>
        {
            var context = new DefaultHttpContext();
            var middleware = new ExceptionNotificationMiddleware(request =>
            {
                request.Response.StatusCode = 204;
                return Task.CompletedTask;
            }, logger);
            await middleware.InvokeAsync(context, notifications);
            Assert.Equal(204, context.Response.StatusCode);
            Assert.Empty(recorded);
        }, expectedFault: false);
    }

    private static async Task WithLoggerAsync(
        Func<ILogger<ExceptionNotificationMiddleware>, INotificationService, ConcurrentQueue<CreateNotificationRequest>, Task> run,
        bool expectedFault)
    {
        var recorded = new ConcurrentQueue<CreateNotificationRequest>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = Substitute.For<INotificationService>();
        notifications.CreateNotificationAsync(Arg.Any<CreateNotificationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var item = call.Arg<CreateNotificationRequest>();
            recorded.Enqueue(item);
            if (item.Message.Contains("queue barrier", StringComparison.Ordinal)) drained.TrySetResult();
            return Task.CompletedTask;
        });
        using var services = new ServiceCollection().AddSingleton(notifications).BuildServiceProvider();
        using var provider = new NotificationLoggerProvider(services.GetRequiredService<IServiceScopeFactory>(), "Test", "API", "API");
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var logger = factory.CreateLogger<ExceptionNotificationMiddleware>();
        await run(logger, notifications, recorded);
        // A real queued warning establishes that all prior logger entries have been
        // processed; absence assertions do not depend on a sleep or a cancelled write.
        logger.LogWarning("queue barrier");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedFault ? 1 : 0, recorded.Count(item => item.Severity == NotificationSeverity.Error));
        Assert.Equal(expectedFault ? 1 : 0, recorded.Count(item => item.Severity == NotificationSeverity.Critical));
    }

    private sealed class TimeoutFeature(CancellationToken token) : IHttpRequestTimeoutFeature
    {
        public CancellationToken RequestTimeoutToken => token;
        public void DisableTimeout() { }
    }
    private sealed class BlockingAccessService(TaskCompletionSource entered) : ICurrentUserAccessService
    {
        public async Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var pending = new TaskCompletionSource<CurrentUserAccessProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => pending.TrySetCanceled(ct));
            entered.TrySetResult();
            return await pending.Task;
        }
    }
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
