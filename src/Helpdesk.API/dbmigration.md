Correct Command

From solution root:

dotnet ef migrations add DescriptiveMigrationName --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Infrastructure --context HelpdeskDbContext


--project → where the DbContext & migrations live
--startup-project → which project provides configuration (connection string, DI)

Use the existing design-time factory and configured design-time connection.
Scaffold the corresponding SQLite migration in
`src/Helpdesk.Infrastructure.SqliteMigrations` with `--context HelpdeskDbContext`
and `--output-dir Migrations/Helpdesk`. Keep shipped migrations unchanged.

Then Apply Migration
dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.API --context HelpdeskDbContext
