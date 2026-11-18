using Microsoft.AspNetCore.Identity;

namespace BirdLab.Api.Models;

public sealed class ApplicationUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuthSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class SubscriptionPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long Price { get; set; }
    public string Currency { get; set; } = "USDC";
    public int DurationDays { get; set; }
    public int AnalysisLimit { get; set; }
    public string Features { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

public sealed class UserSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public int PlanId { get; set; }
    public Guid PaymentOrderId { get; set; }
    public Guid? ReplacedByPaymentOrderId { get; set; }
    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }
    public string Status { get; set; } = "Active";
    public int AnalysisLimit { get; set; }
    public string Features { get; set; } = "[]";
    public int AnalysesUsed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PaymentOrder
{
    public Guid? NetworkInstanceId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int PlanId { get; set; }
    public string WalletAddress { get; set; } = "";
    public long ChainId { get; set; }
    public string TokenContractAddress { get; set; } = "";
    public string PaymentContractAddress { get; set; } = "";
    public long Amount { get; set; }
    public int TokenDecimals { get; set; }
    public int DurationDays { get; set; }
    public int AnalysisLimit { get; set; }
    public string Features { get; set; } = "[]";
    public bool ReplaceCurrentPlan { get; set; }
    public string Status { get; set; } = "Created";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CryptoPayment
{
    public Guid? NetworkInstanceId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentOrderId { get; set; }
    public string UserId { get; set; } = "";
    public int SubscriptionPlanId { get; set; }
    public string OrderId { get; set; } = "";
    public string WalletAddress { get; set; } = "";
    public string BlockchainNetwork { get; set; } = "";
    public long ChainId { get; set; }
    public string TokenContractAddress { get; set; } = "";
    public string PaymentContractAddress { get; set; } = "";
    public long Amount { get; set; }
    public string TransactionHash { get; set; } = "";
    public long? BlockNumber { get; set; }
    public string? BlockHash { get; set; }
    public int? LogIndex { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAt { get; set; }
}

public sealed class UserWallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string WalletAddress { get; set; } = "";
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class WalletNonce
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string WalletAddress { get; set; } = "";
    public string Message { get; set; } = "";
    public string Domain { get; set; } = "";
    public long ChainId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class UsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string UserId { get; set; } = "";
    public Guid? UserSubscriptionId { get; set; }
    public string Feature { get; set; } = "";
    public string Status { get; set; } = "Reserved";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class LocalNetworkInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long ChainId { get; set; } = 31337;
    public string Phase { get; set; } = "Planned";
    public bool IsCurrent { get; set; }
    public Guid? TransitionBatchId { get; set; }
    public string? DeploymentJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LocalTransitionBatch
{
    public Guid Id { get; set; }
    public Guid LegacyNetworkInstanceId { get; set; }
    public Guid TargetNetworkInstanceId { get; set; }
    public string BackupSha256 { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string ApprovalReference { get; set; } = "";
    public string SourceProjectionJson { get; set; } = "";
    public DateTimeOffset CutoffUtc { get; set; }
    public DateTimeOffset PreparedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LegacyEntitlementProvenance
{
    public Guid UserSubscriptionId { get; set; }
    public Guid SourcePaymentId { get; set; }
    public Guid LegacyNetworkInstanceId { get; set; }
    public Guid TransitionBatchId { get; set; }
    public string AccessSource { get; set; } = "LegacyLocalSnapshot";
    public string Policy { get; set; } = "HonorExistingUntilOriginalEnd";
    public DateTimeOffset OriginalStartDate { get; set; }
    public DateTimeOffset OriginalEndDate { get; set; }
    public int OriginalAnalysisLimit { get; set; }
    public int OriginalAnalysesUsed { get; set; }
    public string OriginalFeatures { get; set; } = "[]";
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
