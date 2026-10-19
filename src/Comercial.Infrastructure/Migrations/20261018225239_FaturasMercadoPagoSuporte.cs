using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FaturasMercadoPagoSuporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupportPreAuthorizedAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupportPreAuthorizedByUserId",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportPreAuthorizedOperations",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableNotifiedAt",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutPreferenceId",
                table: "invoices",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutUrl",
                table: "invoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "MpPaymentId",
                table: "invoices",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "invoices",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaidMethod",
                table: "invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PixCopyPaste",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PixExpiresAt",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PixQrBase64",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderSentAt",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "platform_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    IsSecret = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAdminId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_Status_DueAt",
                table: "invoices",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_TenantId_SubscriptionId_PeriodStart",
                table: "invoices",
                columns: new[] { "TenantId", "SubscriptionId", "PeriodStart" },
                unique: true,
                filter: "\"Status\" <> 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_settings");

            migrationBuilder.DropIndex(
                name: "IX_invoices_Status_DueAt",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_TenantId_SubscriptionId_PeriodStart",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "SupportPreAuthorizedAt",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "SupportPreAuthorizedByUserId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "SupportPreAuthorizedOperations",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AvailableNotifiedAt",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "CheckoutPreferenceId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "CheckoutUrl",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MpPaymentId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "PaidMethod",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "PixCopyPaste",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "PixExpiresAt",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "PixQrBase64",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "invoices");
        }
    }
}
