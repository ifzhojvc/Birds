using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BirdLab.Api.Migrations
{
    public partial class LocalNetworkIdentityAndLegacyEntitlements : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CryptoPayments_ChainId_TransactionHash_LogIndex",
                table: "CryptoPayments");

            migrationBuilder.AddColumn<Guid>(
                name: "NetworkInstanceId",
                table: "PaymentOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NetworkInstanceId",
                table: "CryptoPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocalTransitionBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegacyNetworkInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetNetworkInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackupSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApprovalReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceProjectionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CutoffUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalTransitionBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalNetworkInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChainId = table.Column<long>(type: "bigint", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    TransitionBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeploymentJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalNetworkInstances", x => x.Id);
                    table.CheckConstraint("CK_LocalNetwork_State", "[ChainId] = 31337 AND [Phase] IN ('ArchivedUnverified','Planned','Deployed') AND (([Phase] = 'ArchivedUnverified' AND [IsCurrent] = 0) OR ([Phase] IN ('Planned','Deployed') AND [IsCurrent] = 1))");
                    table.ForeignKey(
                        name: "FK_LocalNetworkInstances_LocalTransitionBatches_TransitionBatchId",
                        column: x => x.TransitionBatchId,
                        principalTable: "LocalTransitionBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegacyEntitlementProvenances",
                columns: table => new
                {
                    UserSubscriptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourcePaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegacyNetworkInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitionBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccessSource = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Policy = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    OriginalStartDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OriginalEndDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OriginalAnalysisLimit = table.Column<int>(type: "int", nullable: false),
                    OriginalAnalysesUsed = table.Column<int>(type: "int", nullable: false),
                    OriginalFeatures = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyEntitlementProvenances", x => x.UserSubscriptionId);
                    table.ForeignKey(
                        name: "FK_LegacyEntitlementProvenances_CryptoPayments_SourcePaymentId",
                        column: x => x.SourcePaymentId,
                        principalTable: "CryptoPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyEntitlementProvenances_LocalNetworkInstances_LegacyNetworkInstanceId",
                        column: x => x.LegacyNetworkInstanceId,
                        principalTable: "LocalNetworkInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyEntitlementProvenances_LocalTransitionBatches_TransitionBatchId",
                        column: x => x.TransitionBatchId,
                        principalTable: "LocalTransitionBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyEntitlementProvenances_UserSubscriptions_UserSubscriptionId",
                        column: x => x.UserSubscriptionId,
                        principalTable: "UserSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_NetworkInstanceId",
                table: "PaymentOrders",
                column: "NetworkInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CryptoPayments_ChainId_TransactionHash_LogIndex",
                table: "CryptoPayments",
                columns: new[] { "ChainId", "TransactionHash", "LogIndex" },
                unique: true,
                filter: "[LogIndex] IS NOT NULL AND [ChainId] <> 31337");

            migrationBuilder.CreateIndex(
                name: "IX_CryptoPayments_NetworkInstanceId_TransactionHash_LogIndex",
                table: "CryptoPayments",
                columns: new[] { "NetworkInstanceId", "TransactionHash", "LogIndex" },
                unique: true,
                filter: "[LogIndex] IS NOT NULL AND [NetworkInstanceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyEntitlementProvenances_LegacyNetworkInstanceId",
                table: "LegacyEntitlementProvenances",
                column: "LegacyNetworkInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyEntitlementProvenances_SourcePaymentId",
                table: "LegacyEntitlementProvenances",
                column: "SourcePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyEntitlementProvenances_TransitionBatchId",
                table: "LegacyEntitlementProvenances",
                column: "TransitionBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalNetworkInstances_ChainId",
                table: "LocalNetworkInstances",
                column: "ChainId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LocalNetworkInstances_TransitionBatchId",
                table: "LocalNetworkInstances",
                column: "TransitionBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalTransitionBatches_TargetNetworkInstanceId",
                table: "LocalTransitionBatches",
                column: "TargetNetworkInstanceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CryptoPayments_LocalNetworkInstances_NetworkInstanceId",
                table: "CryptoPayments",
                column: "NetworkInstanceId",
                principalTable: "LocalNetworkInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentOrders_LocalNetworkInstances_NetworkInstanceId",
                table: "PaymentOrders",
                column: "NetworkInstanceId",
                principalTable: "LocalNetworkInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CryptoPayments_LocalNetworkInstances_NetworkInstanceId",
                table: "CryptoPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentOrders_LocalNetworkInstances_NetworkInstanceId",
                table: "PaymentOrders");

            migrationBuilder.DropTable(
                name: "LegacyEntitlementProvenances");

            migrationBuilder.DropTable(
                name: "LocalNetworkInstances");

            migrationBuilder.DropTable(
                name: "LocalTransitionBatches");

            migrationBuilder.DropIndex(
                name: "IX_PaymentOrders_NetworkInstanceId",
                table: "PaymentOrders");

            migrationBuilder.DropIndex(
                name: "IX_CryptoPayments_ChainId_TransactionHash_LogIndex",
                table: "CryptoPayments");

            migrationBuilder.DropIndex(
                name: "IX_CryptoPayments_NetworkInstanceId_TransactionHash_LogIndex",
                table: "CryptoPayments");

            migrationBuilder.DropColumn(
                name: "NetworkInstanceId",
                table: "PaymentOrders");

            migrationBuilder.DropColumn(
                name: "NetworkInstanceId",
                table: "CryptoPayments");

            migrationBuilder.CreateIndex(
                name: "IX_CryptoPayments_ChainId_TransactionHash_LogIndex",
                table: "CryptoPayments",
                columns: new[] { "ChainId", "TransactionHash", "LogIndex" },
                unique: true,
                filter: "[LogIndex] IS NOT NULL");
        }
    }
}
