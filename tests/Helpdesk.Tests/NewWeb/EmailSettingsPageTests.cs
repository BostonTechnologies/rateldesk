namespace Helpdesk.Tests.NewWeb;

public class EmailSettingsPageTests
{
    private static readonly string Source = ReadPageSource();

    [Fact]
    public void PageSelectsExplicitGlobalAssignmentWithoutWritingDefaults()
    {
        Assert.Contains("@page \"/admin/email-settings\"", Source);
        Assert.Contains("@attribute [Authorize(Roles = \"HelpdeskAdmin\")]", Source);
        Assert.Contains("GetFromJsonAsync<List<EmailInboxSettingsDto>>(\"/api/v1/email-settings\"", Source);
        Assert.Contains("mailboxes.SingleOrDefault(x => !x.Archived && x.Scope == MailboxScope.Global)", Source);
        Assert.Contains("savedBaseline = model.Clone();", Source);
        Assert.DoesNotContain("PostAsJsonAsync(\"/api/v1/email-settings\"", Source[..Source.IndexOf("private async Task SaveAsync", StringComparison.Ordinal)]);
    }

    [Fact]
    public void PageSavesExistingRecordWithPutAndConsumesTheSavedDto()
    {
        Assert.Contains("HelpdeskApi.PutAsJsonAsync($\"/api/v1/email-settings/{model.Id}\"", Source);
        Assert.Contains("ReadFromJsonAsync<EmailInboxSettingsDto>", Source);
        Assert.Contains("model = EmailInboxSettingsForm.FromDto(saved);", Source);
        Assert.Contains("isDirty = false;", Source);
        Assert.DoesNotContain("/api/v1/email/imap/save", Source);
    }

    [Fact]
    public void PageTestsTheCurrentDraftWithoutLegacyImapEndpoint()
    {
        Assert.Contains("HelpdeskApi.PostAsJsonAsync(\"/api/v1/email-settings/test\"", Source);
        Assert.DoesNotContain("/api/v1/email/imap/test", Source);
        Assert.Contains("testResult = null;", Source);
    }

    [Fact]
    public void PageBindsMailboxSslAndIndependentIngestionFields()
    {
        Assert.Contains("@bind-Value=\"model.MailboxAddress\"", Source);
        Assert.Contains("@bind-Value=\"model.TlsMode\"", Source);
        Assert.Contains("@bind-Value=\"model.Enabled\"", Source);
        Assert.Contains("@bind-Value=\"model.BackgroundSyncEnabled\"", Source);
        Assert.Contains("Label=\"Mailbox enabled\"", Source);
        Assert.Contains("Label=\"Background ingestion enabled\"", Source);
    }

    [Fact]
    public void BlankClientSecretIsNotSentAsANewSecret()
    {
        Assert.Contains("FromDto(EmailInboxSettingsDto dto)", Source);
        Assert.DoesNotContain("ClientSecret = dto.", Source);
        Assert.Contains("A client secret is configured. Leave this field blank to keep it.", Source);
        Assert.Contains("model = EmailInboxSettingsForm.FromDto(saved);", Source);
    }

    [Fact]
    public void PageProtectsUnsavedChangesAndCanDiscardThem()
    {
        Assert.Contains("<NavigationLock ConfirmExternalNavigation=\"@(isDirty || outgoingDirty)\"", Source);
        Assert.Contains("ConfirmInternalNavigationAsync", Source);
        Assert.Contains("model = savedBaseline.Clone();", Source);
        Assert.Contains("testResult = null;", Source);
    }

    [Fact]
    public void PageShowsRetryInsteadOfAnEditableDefaultFormWhenLoadFails()
    {
        Assert.Contains("else if (!loaded)", Source);
        Assert.Contains("Retry before making changes.", Source);
        Assert.Contains("private bool mutationsDisabled => loading || !loaded || saving || testing;", Source);
    }

    private static string ReadPageSource()
    {
        var path = Path.Combine(
            TestEnvironment.RepositoryRoot,
            "src",
            "HelpDesk.NewWeb",
            "Components",
            "Pages",
            "Admin",
            "Email",
            "EmailSettings.razor");
        return File.ReadAllText(path);
    }
}
