using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalRChat.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_Name", "char_length(btrim(\"Name\")) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMembers",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeftAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMembers", x => new { x.ConversationId, x.UserId });
                    table.CheckConstraint("CK_ConversationMembers_LeftAfterJoined", "\"LeftAtUtc\" IS NULL OR \"LeftAtUtc\" >= \"JoinedAtUtc\"");
                    table.CheckConstraint("CK_ConversationMembers_OwnerIsActive", "\"Role\" <> 'Owner' OR \"LeftAtUtc\" IS NULL");
                    table.CheckConstraint("CK_ConversationMembers_Role", "\"Role\" IN ('Owner', 'Member')");
                    table.ForeignKey(
                        name: "FK_ConversationMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMembers_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_ConversationId_LeftAtUtc",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "LeftAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_ConversationId_Role",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "Role" },
                unique: true,
                filter: "\"Role\" = 'Owner' AND \"LeftAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_UserId_LeftAtUtc_ConversationId",
                table: "ConversationMembers",
                columns: new[] { "UserId", "LeftAtUtc", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedAtUtc_Id",
                table: "Conversations",
                columns: new[] { "CreatedAtUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedByUserId",
                table: "Conversations",
                column: "CreatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMembers");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
