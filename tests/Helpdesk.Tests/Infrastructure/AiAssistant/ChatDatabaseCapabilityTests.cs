using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatDatabaseCapabilityTests
{
    [Theory]
    [InlineData("Sqlite", false, true)]
    [InlineData("Sqlite", true, false)]
    [InlineData("PostgreSql", true, true)]
    public void Native_chat_validates_database_capability_at_startup(string provider, bool enabled, bool expectedValid)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rateldesk-chat-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["Database:Sqlite:Path"] = Path.Combine(directory, "app.db"),
                ["ConnectionStrings:HelpdeskDb"] = provider == "PostgreSql" ? "Host=localhost;Database=capability_test" : null,
                ["AiAssistantChat:Enabled"] = enabled.ToString(),
                ["AiAssistantChat:Endpoint"] = "https://chat.example.test/hub/session",
                ["AiAssistantChat:DeviceToken"] = "test-device-token"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHelpdeskInfrastructure(configuration);
            using var serviceProvider = services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<AiAssistantChatOptions>>();

            if (expectedValid)
            {
                Assert.Equal(enabled, options.Value.Enabled);
            }
            else
            {
                var error = Assert.Throws<OptionsValidationException>(() => options.Value);
                Assert.Contains("requires PostgreSQL", error.Message, StringComparison.Ordinal);
                Assert.Contains("Netclaw:Enabled=false", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
