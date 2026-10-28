using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FiscalContabilidade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fiscal_events_TenantId",
                table: "fiscal_events");

            migrationBuilder.AddColumn<int>(
                name: "ArchiveAttempts",
                table: "fiscal_number_voids",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchiveNextAt",
                table: "fiscal_number_voids",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XmlUrl",
                table: "fiscal_number_voids",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArchiveAttempts",
                table: "fiscal_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchiveNextAt",
                table: "fiscal_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelXmlUrl",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentName",
                table: "email_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentPath",
                table: "email_messages",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "accountant_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalExportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Recipients = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EmailMessageIds = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accountant_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_accountant_deliveries_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accountant_settings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ContactName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ExtraEmails = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AutoSend = table.Column<bool>(type: "boolean", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: false),
                    IncludeXml = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeReports = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accountant_settings", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_accountant_settings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_exports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StorageName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocumentCount = table.Column<int>(type: "integer", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    VoidCount = table.Column<int>(type: "integer", nullable: false),
                    ReportCount = table.Column<int>(type: "integer", nullable: false),
                    MissingCount = table.Column<int>(type: "integer", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_exports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fiscal_exports_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_month_closings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChecksJson = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastExportId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_month_closings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fiscal_month_closings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_xml_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    NumberVoidId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Model = table.Column<int>(type: "integer", nullable: false),
                    AccessKey = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: true),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ReferenceAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_xml_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fiscal_xml_files_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_events_TenantId_FiscalDocumentId",
                table: "fiscal_events",
                columns: new[] { "TenantId", "FiscalDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_accountant_deliveries_TenantId_Year_Month_Trigger",
                table: "accountant_deliveries",
                columns: new[] { "TenantId", "Year", "Month", "Trigger" });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_exports_TenantId_Year_Month_CreatedAt",
                table: "fiscal_exports",
                columns: new[] { "TenantId", "Year", "Month", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_month_closings_TenantId_Year_Month",
                table: "fiscal_month_closings",
                columns: new[] { "TenantId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_xml_files_TenantId_FiscalDocumentId_Kind",
                table: "fiscal_xml_files",
                columns: new[] { "TenantId", "FiscalDocumentId", "Kind" },
                unique: true,
                filter: "\"FiscalDocumentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_xml_files_TenantId_NumberVoidId",
                table: "fiscal_xml_files",
                columns: new[] { "TenantId", "NumberVoidId" },
                unique: true,
                filter: "\"NumberVoidId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_xml_files_TenantId_ReferenceAt",
                table: "fiscal_xml_files",
                columns: new[] { "TenantId", "ReferenceAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accountant_deliveries");

            migrationBuilder.DropTable(
                name: "accountant_settings");

            migrationBuilder.DropTable(
                name: "fiscal_exports");

            migrationBuilder.DropTable(
                name: "fiscal_month_closings");

            migrationBuilder.DropTable(
                name: "fiscal_xml_files");

            migrationBuilder.DropIndex(
                name: "IX_fiscal_events_TenantId_FiscalDocumentId",
                table: "fiscal_events");

            migrationBuilder.DropColumn(
                name: "ArchiveAttempts",
                table: "fiscal_number_voids");

            migrationBuilder.DropColumn(
                name: "ArchiveNextAt",
                table: "fiscal_number_voids");

            migrationBuilder.DropColumn(
                name: "XmlUrl",
                table: "fiscal_number_voids");

            migrationBuilder.DropColumn(
                name: "ArchiveAttempts",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "ArchiveNextAt",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "CancelXmlUrl",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "AttachmentName",
                table: "email_messages");

            migrationBuilder.DropColumn(
                name: "AttachmentPath",
                table: "email_messages");

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_events_TenantId",
                table: "fiscal_events",
                column: "TenantId");
        }
    }
}
