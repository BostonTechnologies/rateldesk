namespace Helpdesk.Shared.DTOs;

/// <summary>A bounded creation lookup; eligibility is counted before applying the search.</summary>
public sealed record CreationLookupResult<T>(IReadOnlyList<T> Items, int TotalEligible, bool HasMore);
