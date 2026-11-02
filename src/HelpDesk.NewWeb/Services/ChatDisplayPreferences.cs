namespace HelpDesk.NewWeb.Services;

public sealed class ChatDisplayPreferences
{
    public bool Compact { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowIdentifiers { get; set; }
    public bool AutoExpand { get; set; }
    public bool ShowUsage { get; set; }
    public bool ShowProcessing { get; set; }
    public bool ShowCompaction { get; set; }
    public bool ShowSubagents { get; set; }
}
