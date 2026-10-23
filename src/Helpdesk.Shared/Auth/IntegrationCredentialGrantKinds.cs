namespace Helpdesk.Shared.Auth;

/// <summary>Explicit, persisted kinds of account-bound integration access.</summary>
public static class IntegrationCredentialGrantKinds
{
    public const string Ordinary = "ordinary";
    public const string OwnerAdministration = "owner-administration";
    public const string OwnerAdministrationPurpose = "mcp-owner";
}
