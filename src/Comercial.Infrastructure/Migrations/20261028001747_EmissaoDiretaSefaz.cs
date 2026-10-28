using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmissaoDiretaSefaz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressNumber",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Cep",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateNotAfter",
                table: "tenant_fiscal_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificatePasswordProtected",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateProtected",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateSubject",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateThumbprint",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CityCode",
                table: "tenant_fiscal_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CityName",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CscId",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CscProtected",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NfceNextNumber",
                table: "tenant_fiscal_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QrCodeVersion",
                table: "tenant_fiscal_profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Street",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TradeName",
                table: "tenant_fiscal_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsultUrl",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmissionAt",
                table: "fiscal_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumericCode",
                table: "fiscal_documents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_fiscal_documents_TenantId_Model_Series_Number",
                table: "fiscal_documents",
                columns: new[] { "TenantId", "Model", "Series", "Number" },
                unique: true,
                filter: "\"Number\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fiscal_documents_TenantId_Model_Series_Number",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "AddressNumber",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "Cep",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CertificateNotAfter",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CertificatePasswordProtected",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CertificateProtected",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CertificateSubject",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CertificateThumbprint",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CityCode",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CityName",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CscId",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "CscProtected",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "District",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "LegalName",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "NfceNextNumber",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "QrCodeVersion",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "Street",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "TradeName",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "ConsultUrl",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "EmissionAt",
                table: "fiscal_documents");

            migrationBuilder.DropColumn(
                name: "NumericCode",
                table: "fiscal_documents");
        }
    }
}
