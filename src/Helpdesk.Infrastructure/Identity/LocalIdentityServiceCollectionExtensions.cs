using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure.Identity;

public static class LocalIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddRatelDeskLocalIdentity(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Password.RequiredLength = Helpdesk.Shared.Auth.LocalPasswordPolicy.MinimumLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<RatelDeskIdentityDbContext>()
            .AddDefaultTokenProviders();

        return services;
    }
}
