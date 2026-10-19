using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FiscalNfceNfse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cest",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cfop",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FiscalOrigin",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IbsCbsClass",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IbsCbsCst",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IcmsCst",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ncm",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "fiscal_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DanfeUrl",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Environment",
                table: "fiscal_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "fiscal_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "fiscal_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderRef",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QrCodeUrl",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Series",
                table: "fiscal_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XmlUrl",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fiscal_number_voids",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Series = table.Column<int>(type: "integer", nullable: false),
                    FirstNumber = table.Column<int>(type: "integer", nullable: false),
                    LastNumber = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Protocol = table.Column<string>(type: "text", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_number_voids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fiscal_number_voids_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nfse_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: true),
                    VerificationCode = table.Column<string>(type: "text", nullable: true),
                    PdfUrl = table.Column<string>(type: "text", nullable: true),
                    LastMessage = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nfse_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_fiscal_profiles",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cnpj = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    StateRegistration = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    Uf = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderTokenProtected = table.Column<string>(type: "text", nullable: true),
                    NfceSeries = table.Column<int>(type: "integer", nullable: false),
                    AutoEmit = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultCfop = table.Column<string>(type: "text", nullable: false),
                    DefaultIcmsCst = table.Column<string>(type: "text", nullable: false),
                    DefaultPisCst = table.Column<string>(type: "text", nullable: false),
                    DefaultCofinsCst = table.Column<string>(type: "text", nullable: false),
                    DefaultIbsCbsCst = table.Column<string>(type: "text", nullable: false),
                    DefaultIbsCbsClass = table.Column<string>(type: "text", nullable: false),
                    IbsRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    CbsRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_fiscal_profiles", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_tenant_fiscal_profiles_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_number_voids_TenantId",
                table: "fiscal_number_voids",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_nfse_documents_InvoiceId",
                table: "nfse_documents",
                column: "InvoiceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fiscal_number_voids");

            migrationBuilder.DropTable(
                name: "nfse_documents");

            migrationBuilder.DropTable(
                name: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "Cest",
                table: "products");

            migrationBuilder.DropColumn(
                name: "Cfop",
                table: "products");

            migrationBuilder.DropColumn(
                name: "FiscalOrigin",
                table: "products");

            migrationBuilder.DropColumn(
                name: "IbsCbsClass",
                table: "products");

            migrationBuilder.DropColumn(
                name: "IbsCbsCst",
                table: "products");

            migrationBuilder.DropColumn(
                name: "IcmsCst",
                table: "products");

            migrationBuilder.DropColumn(
                name: "Ncm",
                table: "products");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "DanfeUrl",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "ProviderRef",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "QrCodeUrl",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "Series",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "XmlUrl",
                table: "fiscal_documents");
        }
    }
}
