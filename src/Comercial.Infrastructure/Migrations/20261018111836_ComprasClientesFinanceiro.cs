using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Comercial.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ComprasClientesFinanceiro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LoyaltyDiscountCents",
                table: "sales",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyPointsRedeemed",
                table: "sales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ReturnedCents",
                table: "sales",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Document = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    MarketingConsent = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnonymizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                    table.UniqueConstraint("AK_customers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_customers_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_titles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    CostCenter = table.Column<string>(type: "text", nullable: true),
                    Counterparty = table.Column<string>(type: "text", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidAmountCents = table.Column<long>(type: "bigint", nullable: true),
                    PaymentMethod = table.Column<string>(type: "text", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallmentNumber = table.Column<int>(type: "integer", nullable: false),
                    InstallmentCount = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_titles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_financial_titles_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_programs",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PointsPerReal = table.Column<int>(type: "integer", nullable: false),
                    CentsPerPoint = table.Column<int>(type: "integer", nullable: false),
                    MinPointsToRedeem = table.Column<int>(type: "integer", nullable: false),
                    ExpirationDays = table.Column<int>(type: "integer", nullable: false),
                    MaxRedeemPercent = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_programs", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_loyalty_programs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    DivergenceJson = table.Column<string>(type: "text", nullable: true),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_purchase_receipts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_returns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Restock = table.Column<bool>(type: "boolean", nullable: false),
                    RefundMethod = table.Column<string>(type: "text", nullable: false),
                    RefundCents = table.Column<long>(type: "bigint", nullable: false),
                    CostCents = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_returns", x => x.Id);
                    table.UniqueConstraint("AK_sale_returns_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_sale_returns_sales_TenantId_SaleId",
                        columns: x => new { x.TenantId, x.SaleId },
                        principalTable: "sales",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_returns_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Document = table.Column<string>(type: "text", nullable: true),
                    ContactName = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.Id);
                    table.UniqueConstraint("AK_suppliers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_suppliers_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    UniqueKey = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loyalty_transactions_customers_TenantId_CustomerId",
                        columns: x => new { x.TenantId, x.CustomerId },
                        principalTable: "customers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_transactions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_return_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    RefundCents = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_return_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_return_items_sale_returns_TenantId_SaleReturnId",
                        columns: x => new { x.TenantId, x.SaleReturnId },
                        principalTable: "sale_returns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_return_items_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ExpectedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.Id);
                    table.UniqueConstraint("AK_purchase_orders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_purchase_orders_stores_TenantId_StoreId",
                        columns: x => new { x.TenantId, x.StoreId },
                        principalTable: "stores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_orders_suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalTable: "suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_orders_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    QuantityOrdered = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCostCents = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_purchase_items_products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalTable: "products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_items_purchase_orders_TenantId_PurchaseOrderId",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchase_items_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_TenantId_CustomerId",
                table: "sales",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_TenantId_Document",
                table: "customers",
                columns: new[] { "TenantId", "Document" },
                unique: true,
                filter: "\"Document\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customers_TenantId_Name",
                table: "customers",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_financial_titles_TenantId_GroupId",
                table: "financial_titles",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_financial_titles_TenantId_Kind_Status_DueDate",
                table: "financial_titles",
                columns: new[] { "TenantId", "Kind", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_transactions_TenantId_CustomerId",
                table: "loyalty_transactions",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_transactions_TenantId_UniqueKey",
                table: "loyalty_transactions",
                columns: new[] { "TenantId", "UniqueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_items_TenantId_ProductId",
                table: "purchase_items",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_items_TenantId_PurchaseOrderId",
                table: "purchase_items",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TenantId_Number",
                table: "purchase_orders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TenantId_StoreId",
                table: "purchase_orders",
                columns: new[] { "TenantId", "StoreId" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TenantId_SupplierId",
                table: "purchase_orders",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_receipts_TenantId_PurchaseOrderId",
                table: "purchase_receipts",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_return_items_TenantId_SaleReturnId",
                table: "sale_return_items",
                columns: new[] { "TenantId", "SaleReturnId" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_returns_TenantId_CreatedAt",
                table: "sale_returns",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_returns_TenantId_SaleId",
                table: "sale_returns",
                columns: new[] { "TenantId", "SaleId" });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_TenantId_Name",
                table: "suppliers",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_sales_customers_TenantId_CustomerId",
                table: "sales",
                columns: new[] { "TenantId", "CustomerId" },
                principalTable: "customers",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sales_customers_TenantId_CustomerId",
                table: "sales");

            migrationBuilder.DropTable(
                name: "financial_titles");

            migrationBuilder.DropTable(
                name: "loyalty_programs");

            migrationBuilder.DropTable(
                name: "loyalty_transactions");

            migrationBuilder.DropTable(
                name: "purchase_items");

            migrationBuilder.DropTable(
                name: "purchase_receipts");

            migrationBuilder.DropTable(
                name: "sale_return_items");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropTable(
                name: "sale_returns");

            migrationBuilder.DropTable(
                name: "suppliers");

            migrationBuilder.DropIndex(
                name: "IX_sales_TenantId_CustomerId",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountCents",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "LoyaltyPointsRedeemed",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "ReturnedCents",
                table: "sales");
        }
    }
}
