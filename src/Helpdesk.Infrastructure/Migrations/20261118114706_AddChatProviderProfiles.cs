using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatProviderProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderActorId",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderKind",
                table: "AiAssistantChatConversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderProfileId",
                table: "AiAssistantChatConversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderRevision",
                table: "AiAssistantChatConversations",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AiAssistantChatConversations"
                SET "ProviderProfileId" = '90b1a190-f989-4cf8-ac56-0ce23b566840'
                WHERE "ProviderProfileFingerprint" IS NOT NULL
                  AND length("ProviderProfileFingerprint") = 64
                """);

            migrationBuilder.CreateTable(
                name: "ChatProviderPreference",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatProviderPreference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatArtifact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false),
                    TurnId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Contents = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatArtifact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenAiChatArtifact_AiAssistantChatConversations_Conversatio~",
                        column: x => x.ConversationId,
                        principalTable: "AiAssistantChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatDecision",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<string>(type: "text", nullable: false),
                    RemoteActionId = table.Column<string>(type: "text", nullable: false),
                    TurnId = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    Arguments = table.Column<string>(type: "text", nullable: false),
                    ArgumentHash = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatDecision", x => new { x.ConversationId, x.CallId });
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatDispatch",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    TurnId = table.Column<string>(type: "text", nullable: true),
                    AdmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelSentAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextObservationAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatDispatch", x => new { x.ConversationId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_OpenAiChatDispatch_AiAssistantChatConversations_Conversatio~",
                        column: x => x.ConversationId,
                        principalTable: "AiAssistantChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatEventReceipt",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatEventReceipt", x => new { x.ConversationId, x.RemoteKey });
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastVerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastVerification = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatProfile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatSessionGrant",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CleanupRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CleanupNextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemoteDeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatSessionGrant", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_OpenAiChatSessionGrant_AiAssistantChatConversations_Convers~",
                        column: x => x.ConversationId,
                        principalTable: "AiAssistantChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenAiChatProfileRevision",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    ProtectedKey = table.Column<string>(type: "text", nullable: false),
                    SkillBundleHash = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiChatProfileRevision", x => new { x.ProfileId, x.Revision });
                    table.ForeignKey(
                        name: "FK_OpenAiChatProfileRevision_OpenAiChatProfile_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "OpenAiChatProfile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenAiChatArtifact_ConversationId_RemoteId",
                table: "OpenAiChatArtifact",
                columns: new[] { "ConversationId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenAiChatDispatch_State_NextObservationAtUtc",
                table: "OpenAiChatDispatch",
                columns: new[] { "State", "NextObservationAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenAiChatSessionGrant_CredentialId",
                table: "OpenAiChatSessionGrant",
                column: "CredentialId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatProviderPreference");

            migrationBuilder.DropTable(
                name: "OpenAiChatArtifact");

            migrationBuilder.DropTable(
                name: "OpenAiChatDecision");

            migrationBuilder.DropTable(
                name: "OpenAiChatDispatch");

            migrationBuilder.DropTable(
                name: "OpenAiChatEventReceipt");

            migrationBuilder.DropTable(
                name: "OpenAiChatProfileRevision");

            migrationBuilder.DropTable(
                name: "OpenAiChatSessionGrant");

            migrationBuilder.DropTable(
                name: "OpenAiChatProfile");

            migrationBuilder.DropColumn(
                name: "ProviderActorId",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "ProviderKind",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "ProviderProfileId",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "ProviderRevision",
                table: "AiAssistantChatConversations");
        }
    }
}
