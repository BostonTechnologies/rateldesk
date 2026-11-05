namespace Helpdesk.Shared.DTOs.Resources;

public class TenantGraphDatasetSettingsDto
{
    public string OrganizationId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool HasStoredSecret { get; set; }
    public bool EnableUsers { get; set; }
    public bool EnableDevices { get; set; }
    public bool EnableGroups { get; set; }
    public bool EnableSharePointSites { get; set; }
    public bool BackgroundSyncEnabled { get; set; }
    public string BackgroundSyncCronExpression { get; set; } = "0 */6 * * *";
    public DateTimeOffset? LastUsersSyncUtc { get; set; }
    public DateTimeOffset? LastDevicesSyncUtc { get; set; }
    public DateTimeOffset? LastGroupsSyncUtc { get; set; }
    public DateTimeOffset? LastSharePointSitesSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncMessage { get; set; }
    public bool HangfireEnabled { get; set; }
    public bool BackgroundSyncRegistered { get; set; }
    public string? BackgroundSyncJobId { get; set; }
    public string? BackgroundSyncQueueName { get; set; }
    public DateTimeOffset? BackgroundSyncNextExecutionUtc { get; set; }
    public DateTimeOffset? BackgroundSyncLastExecutionUtc { get; set; }
    public string HangfireRuntimeSource { get; set; } = "Ops UI / database";
    public string HangfireStorageProvider { get; set; } = "Application PostgreSQL database";
}
