using System.Text.Json;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Helpdesk.Infrastructure.Persistence;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        // ----- converters & comparers for List<string> -----
        var listToString = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : (JsonSerializer.Deserialize<List<string>>(v, JsonSerializerOptions.Default) ?? new List<string>()));

        var listComparer = new ValueComparer<List<string>>(
            (a, b) =>
                ReferenceEquals(a, b) ||
                (a != null && b != null && a.SequenceEqual(b)),
            v => v == null
                ? 0
                : v.Aggregate(0, (h, s) => HashCode.Combine(h, (s == null ? 0 : s.GetHashCode()))),
            v => v == null ? new List<string>() : v.ToList());

        // AllowedCustomerIds
        var custProp = builder.Property(s => s.AllowedCustomerIds);
        custProp.HasConversion(listToString);
        custProp.Metadata.SetValueComparer(listComparer);
        custProp.HasColumnType("TEXT"); // SQLite

        // AllowedOrganizationIds
        var orgProp = builder.Property(s => s.AllowedOrganizationIds);
        orgProp.HasConversion(listToString);
        orgProp.Metadata.SetValueComparer(listComparer);
        orgProp.HasColumnType("TEXT");

        // Service visibility depends on the resolved organization and customer, so it is
        // enforced by the service endpoints. Do not put it in a global query filter:
        // AllowedCustomerIds is value-converted JSON text (whose collection membership
        // EF cannot translate), and a filter configured here would capture scoped context
        // values in EF's cached model.
    }
}
