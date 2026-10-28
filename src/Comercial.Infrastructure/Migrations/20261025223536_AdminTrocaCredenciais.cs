using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdminTrocaCredenciais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CredentialsChangedAt",
                table: "admin_users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CredentialsChangedAt",
                table: "admin_users");
        }
    }
}
