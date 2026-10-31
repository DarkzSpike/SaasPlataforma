using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppCobrancaCampanhas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillingDay",
                table: "customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastChargeSentOn",
                table: "customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPromoSentAt",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_whatsapp_settings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhoneNumberId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "text", nullable: true),
                    DisplayPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VerifiedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AppSecretProtected = table.Column<string>(type: "text", nullable: true),
                    WebhookVerifyToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChargeTemplate = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PromoTemplate = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ChargesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PromotionsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DailyPromoLimit = table.Column<int>(type: "integer", nullable: false),
                    PromoIntervalDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_whatsapp_settings", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_tenant_whatsapp_settings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Sent = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    StopReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_whatsapp_campaigns_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Preview = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_whatsapp_messages_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_campaigns_TenantId_Status_ScheduledAt",
                table: "whatsapp_campaigns",
                columns: new[] { "TenantId", "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_messages_TenantId_CampaignId_CustomerId",
                table: "whatsapp_messages",
                columns: new[] { "TenantId", "CampaignId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_messages_TenantId_Kind_CreatedAt",
                table: "whatsapp_messages",
                columns: new[] { "TenantId", "Kind", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_whatsapp_settings");

            migrationBuilder.DropTable(
                name: "whatsapp_campaigns");

            migrationBuilder.DropTable(
                name: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "BillingDay",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "LastChargeSentOn",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "LastPromoSentAt",
                table: "customers");
        }
    }
}
