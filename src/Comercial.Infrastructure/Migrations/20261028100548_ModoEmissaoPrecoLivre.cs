using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModoEmissaoPrecoLivre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmissionMode",
                table: "tenant_fiscal_profiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "sempre");

            migrationBuilder.AddColumn<bool>(
                name: "OpenPrice",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmissionMode",
                table: "tenant_fiscal_profiles");

            migrationBuilder.DropColumn(
                name: "OpenPrice",
                table: "products");
        }
    }
}
