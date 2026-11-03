using System.Runtime.CompilerServices;

namespace Helpdesk.Tests;

internal static class TestEnvironment
{
    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    internal static string ApplicationAssembly(string projectName, string assemblyFileName)
    {
        // The test output shares ASP.NET's dependency closure. A standalone
        // executable copied there can lose assemblies supplied by that framework.
        // Launch the owning project's output with its matching runtime/deps files.
        var relativeOutput = Path.GetRelativePath(
            Path.Combine(RepositoryRoot, "tests", "Helpdesk.Tests"), AppContext.BaseDirectory);
        var path = Path.GetFullPath(Path.Combine(RepositoryRoot, "src", projectName,
            relativeOutput, assemblyFileName));
        if (!File.Exists(path))
            throw new FileNotFoundException("Build the owning application before running process acceptance.", path);
        return path;
    }

    [ModuleInitializer]
    public static void Initialize()
    {
        // Match Program and DesignTimeDbContextFactory before any PostgreSQL model
        // is cached, including store tests that do not start the API host.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__HelpdeskDb",
            "Host=localhost;Database=helpdesk_tests;Username=helpdesk;Password=helpdesk");
        Environment.SetEnvironmentVariable("Helpdesk__SkipDatabaseStartup", "true");
        // A missed Web-client stub must fail locally, never call a documentation
        // placeholder host with a test authentication header or cookie.
        Environment.SetEnvironmentVariable("ApiBaseUrl", "http://127.0.0.1:9/");
        Environment.SetEnvironmentVariable("ReverseProxy__Clusters__apiCluster__Destinations__api1__Address", "http://127.0.0.1:9/");
        Environment.SetEnvironmentVariable("ExchangeEmail__ClientSecret", "test-client-secret");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Helpdesk.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException($"Could not locate Helpdesk.sln from {AppContext.BaseDirectory}.");
    }
}
