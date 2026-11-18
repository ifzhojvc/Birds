using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirdLab.Api.Subscriptions;

public sealed record TransitionEntitlement(int PlanId, DateTimeOffset StartDate, DateTimeOffset EndDate, int AnalysisLimit, int AnalysesUsed, string[] Features);
public sealed record TransitionSnapshot(string SourceFingerprint, DateTimeOffset EvaluatedAtUtc, SortedDictionary<string, int> Counts, TransitionEntitlement? EligibleEntitlement, string[] Problems, object[] HistoricalPayments);
public sealed class LocalTransitionPlan
{
    public int Version { get; set; } = 1;
    public Guid TransitionId { get; set; }
    public Guid LegacyNetworkInstanceId { get; set; }
    public Guid TargetNetworkInstanceId { get; set; }
    public string SourceBackupSha256 { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string ApprovalReference { get; set; } = "";
    public DateTimeOffset CutoffUtc { get; set; }
    public SortedDictionary<string, int> ExpectedCounts { get; set; } = [];
    public TransitionEntitlement? ExpectedEntitlement { get; set; }
}

public sealed class LocalTransitionService(BirdDbContext db, IBlockchainVerifier verifier, IOptions<BlockchainOptions> settings)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed class SourceOrder
    {
        public Guid Id { get; init; }
        public string UserId { get; init; } = "";
        public string OrderId { get; init; } = "";
        public int PlanId { get; init; }
        public string WalletAddress { get; init; } = "";
        public long ChainId { get; init; }
        public string TokenContractAddress { get; init; } = "";
        public string PaymentContractAddress { get; init; } = "";
        public long Amount { get; init; }
        public int TokenDecimals { get; init; }
        public int DurationDays { get; init; }
        public int AnalysisLimit { get; init; }
        public string Features { get; init; } = "";
        public bool ReplaceCurrentPlan { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }
    private sealed class SourcePayment
    {
        public Guid Id { get; init; }
        public Guid PaymentOrderId { get; init; }
        public string UserId { get; init; } = "";
        public int SubscriptionPlanId { get; init; }
        public string OrderId { get; init; } = "";
        public string WalletAddress { get; init; } = "";
        public string BlockchainNetwork { get; init; } = "";
        public long ChainId { get; init; }
        public string TokenContractAddress { get; init; } = "";
        public string PaymentContractAddress { get; init; } = "";
        public long Amount { get; init; }
        public string TransactionHash { get; init; } = "";
        public long? BlockNumber { get; init; }
        public string? BlockHash { get; init; }
        public int? LogIndex { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public int Attempts { get; init; }
        public DateTimeOffset NextCheckAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ConfirmedAt { get; init; }
    }
    private sealed record SourceState(SourceOrder[] Orders, SourcePayment[] Payments, UserSubscription[] Subscriptions, UsageRecord[] Usage, string[] Users, int Analyses, int Predictions, int Images, int Audio, int FailedImages);

    private async Task<SourceState> ReadSourceAsync(CancellationToken token)
    {
        var orders = await db.PaymentOrders.AsNoTracking().OrderBy(o => o.Id).Select(o => new SourceOrder
        {
            Id = o.Id, UserId = o.UserId, OrderId = o.OrderId, PlanId = o.PlanId, WalletAddress = o.WalletAddress, ChainId = o.ChainId,
            TokenContractAddress = o.TokenContractAddress, PaymentContractAddress = o.PaymentContractAddress, Amount = o.Amount,
            TokenDecimals = o.TokenDecimals, DurationDays = o.DurationDays, AnalysisLimit = o.AnalysisLimit, Features = o.Features,
            ReplaceCurrentPlan = o.ReplaceCurrentPlan, Status = o.Status, CreatedAt = o.CreatedAt, ExpiresAt = o.ExpiresAt
        }).ToArrayAsync(token);
        var payments = await db.CryptoPayments.AsNoTracking().OrderBy(p => p.Id).Select(p => new SourcePayment
        {
            Id = p.Id, PaymentOrderId = p.PaymentOrderId, UserId = p.UserId, SubscriptionPlanId = p.SubscriptionPlanId,
            OrderId = p.OrderId, WalletAddress = p.WalletAddress, BlockchainNetwork = p.BlockchainNetwork, ChainId = p.ChainId,
            TokenContractAddress = p.TokenContractAddress, PaymentContractAddress = p.PaymentContractAddress, Amount = p.Amount,
            TransactionHash = p.TransactionHash, BlockNumber = p.BlockNumber, BlockHash = p.BlockHash, LogIndex = p.LogIndex,
            Status = p.Status, Error = p.Error, Attempts = p.Attempts, NextCheckAt = p.NextCheckAt, CreatedAt = p.CreatedAt, ConfirmedAt = p.ConfirmedAt
        }).ToArrayAsync(token);
        return new(orders, payments, await db.UserSubscriptions.AsNoTracking().OrderBy(s => s.Id).ToArrayAsync(token),
            await db.UsageRecords.AsNoTracking().OrderBy(u => u.Id).ToArrayAsync(token), await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).ToArrayAsync(token),
            await db.Analyses.CountAsync(token), await db.Predictions.CountAsync(token),
            await db.Analyses.CountAsync(a => a.AnalysisType == "image" && a.Status == "Completed", token),
            await db.Analyses.CountAsync(a => a.AnalysisType == "audio" && a.Status == "Completed", token),
            await db.Analyses.CountAsync(a => a.AnalysisType == "image" && a.Status == "Failed", token));
    }

    private static bool Effective(UserSubscription subscription, DateTimeOffset cutoff) => subscription.Status is "Active" or "Scheduled" && subscription.StartDate <= cutoff && subscription.EndDate > cutoff;
    private static UserSubscription[] Eligible(SourceState source, DateTimeOffset cutoff) => source.Subscriptions.Where(s => Effective(s, cutoff) && source.Orders.Any(o => o.Id == s.PaymentOrderId && o.ChainId == 31337 && o.Status == "Paid" && o.Amount > 0) && source.Payments.Any(p => p.PaymentOrderId == s.PaymentOrderId && p.Status == "Confirmed" && p.ConfirmedAt != null)).ToArray();

    private static TransitionSnapshot Snapshot(SourceState source, DateTimeOffset cutoff)
    {
        var eligible = Eligible(source, cutoff);
        var problems = new List<string>();
        if (source.Subscriptions.Any(s => !source.Orders.Any(o => o.Id == s.PaymentOrderId && o.UserId == s.UserId && o.PlanId == s.PlanId))) problems.Add("Subscription/order linkage mismatch.");
        if (source.Orders.Any(o => !source.Users.Contains(o.UserId)) || source.Subscriptions.Any(s => !source.Users.Contains(s.UserId))) problems.Add("Orphaned owner reference.");
        if (source.Payments.Any(p => source.Orders.Count(o => o.Id == p.PaymentOrderId && o.OrderId == p.OrderId && o.UserId == p.UserId && o.PlanId == p.SubscriptionPlanId && o.ChainId == p.ChainId && o.Amount == p.Amount && LocalDeployment.Same(o.WalletAddress, p.WalletAddress) && LocalDeployment.Same(o.TokenContractAddress, p.TokenContractAddress) && LocalDeployment.Same(o.PaymentContractAddress, p.PaymentContractAddress)) != 1)) problems.Add("Payment/order terms mismatch.");
        if (source.Payments.GroupBy(p => p.PaymentOrderId).Any(g => g.Count() != 1)) problems.Add("Ambiguous payments for one order.");
        if (source.Subscriptions.Any(s => s.AnalysesUsed < 0 || s.AnalysesUsed > s.AnalysisLimit || s.StartDate >= s.EndDate)) problems.Add("Invalid subscription dates or quota.");
        if (source.Subscriptions.Where(s => Effective(s, cutoff)).GroupBy(s => s.UserId).Any(g => g.Count() > 1)) problems.Add("Overlapping effective subscriptions.");
        if (source.Subscriptions.Any(s => s.Status == "Scheduled" && s.StartDate > cutoff)) problems.Add("Future paid periods require separate approval.");
        if (source.Usage.Any(u => u.Status == "Reserved" || u.Status is not ("Consumed" or "Released") || !source.Users.Contains(u.UserId) || u.UserSubscriptionId != null && !source.Subscriptions.Any(s => s.Id == u.UserSubscriptionId && s.UserId == u.UserId))) problems.Add("Pending, orphaned or unknown quota records require review.");
        if (eligible.Any(s => source.Usage.Count(u => u.UserSubscriptionId == s.Id && u.Status != "Released") != s.AnalysesUsed)) problems.Add("Active quota does not match usage records.");
        if (source.Subscriptions.Any(s => Effective(s, cutoff) && !eligible.Any(e => e.Id == s.Id))) problems.Add("Effective subscription lacks matching confirmed local payment.");
        if (eligible.Length != 1) problems.Add("The approved transition must preserve exactly one already effective paid subscription.");
        var counts = new SortedDictionary<string, int>
        {
            ["users"] = source.Users.Length, ["analyses"] = source.Analyses, ["predictions"] = source.Predictions,
            ["completedImages"] = source.Images, ["completedAudio"] = source.Audio, ["failedImages"] = source.FailedImages,
            ["orders"] = source.Orders.Length, ["payments"] = source.Payments.Length, ["subscriptions"] = source.Subscriptions.Length,
            ["usageRecords"] = source.Usage.Length, ["consumedUsage"] = source.Usage.Count(u => u.Status == "Consumed"),
            ["reservedUsage"] = source.Usage.Count(u => u.Status == "Reserved"), ["releasedUsage"] = source.Usage.Count(u => u.Status == "Released"),
            ["eligibleSubscriptions"] = eligible.Length, ["paidOrders"] = source.Orders.Count(o => o.Status == "Paid"),
            ["reorgedOrders"] = source.Orders.Count(o => o.Status == "Reorged"), ["confirmedPayments"] = source.Payments.Count(p => p.Status == "Confirmed"),
            ["reorgedPayments"] = source.Payments.Count(p => p.Status == "Reorged"), ["reorgedSubscriptions"] = source.Subscriptions.Count(s => s.Status == "Reorged")
        };
        var original = JsonSerializer.Serialize(new { source.Orders, source.Payments, source.Subscriptions, source.Usage, source.Users, source.Analyses, source.Predictions, source.Images, source.Audio, source.FailedImages });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
        var active = eligible.Length == 1 ? eligible[0] : null;
        return new(fingerprint, cutoff, counts, active is null ? null : new(active.PlanId, active.StartDate, active.EndDate, active.AnalysisLimit, active.AnalysesUsed, SubscriptionService.Features(active.Features)), problems.ToArray(),
            source.Payments.Select(p => (object)new { p.ChainId, p.Status, p.TransactionHash, p.BlockNumber, p.BlockHash, p.LogIndex, p.TokenContractAddress, p.PaymentContractAddress, p.Amount, p.ConfirmedAt }).ToArray());
    }

    public async Task<TransitionSnapshot> InspectAsync(DateTimeOffset cutoff, CancellationToken token) => Snapshot(await ReadSourceAsync(token), cutoff);
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidatePlan(LocalTransitionPlan plan)
    {
        if (plan.Version != 1 || plan.TransitionId == Guid.Empty || plan.LegacyNetworkInstanceId == Guid.Empty || plan.TargetNetworkInstanceId == Guid.Empty || plan.LegacyNetworkInstanceId == plan.TargetNetworkInstanceId || !Digest(plan.SourceBackupSha256) || !Digest(plan.SourceFingerprint) || string.IsNullOrWhiteSpace(plan.ApprovalReference) || plan.ApprovalReference.Length > 256 || plan.CutoffUtc == default || plan.CutoffUtc.Offset != TimeSpan.Zero || plan.CutoffUtc > DateTimeOffset.UtcNow.AddMinutes(5) || plan.ExpectedEntitlement is null || plan.ExpectedCounts is null || plan.ExpectedCounts.Count == 0)
            throw new InvalidOperationException("The explicitly approved transition plan is incomplete or invalid.");
    }

    public async Task<TransitionSnapshot> PrepareAsync(LocalTransitionPlan plan, CancellationToken token)
    {
        ValidatePlan(plan);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var source = await ReadSourceAsync(token);
        var snapshot = Snapshot(source, plan.CutoffUtc);
        if (snapshot.Problems.Length != 0 || !string.Equals(snapshot.SourceFingerprint, plan.SourceFingerprint, StringComparison.OrdinalIgnoreCase) || !snapshot.Counts.SequenceEqual(plan.ExpectedCounts) || JsonSerializer.Serialize(snapshot.EligibleEntitlement) != JsonSerializer.Serialize(plan.ExpectedEntitlement))
            throw new InvalidOperationException("The database does not match the approved backup projection, counts, entitlement or invariants. No transition was applied.");
        var previous = await db.LocalTransitionBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == plan.TransitionId, token);
        if (previous is not null)
        {
            if (previous.BackupSha256 != plan.SourceBackupSha256.ToLowerInvariant() || previous.SourceFingerprint != snapshot.SourceFingerprint || previous.LegacyNetworkInstanceId != plan.LegacyNetworkInstanceId || previous.TargetNetworkInstanceId != plan.TargetNetworkInstanceId || previous.ApprovalReference != plan.ApprovalReference || previous.CutoffUtc != plan.CutoffUtc || await db.PaymentOrders.AnyAsync(o => o.ChainId == 31337 && o.NetworkInstanceId != previous.LegacyNetworkInstanceId, token) || await db.CryptoPayments.AnyAsync(p => p.ChainId == 31337 && p.NetworkInstanceId != previous.LegacyNetworkInstanceId, token) || await db.LegacyEntitlementProvenances.CountAsync(p => p.TransitionBatchId == previous.Id, token) != 1)
                throw new InvalidOperationException("The existing transition differs from the approved idempotent transition.");
            return snapshot;
        }
        if (await db.LocalNetworkInstances.AnyAsync(token) || await db.LocalTransitionBatches.AnyAsync(token) || await db.LegacyEntitlementProvenances.AnyAsync(token) || await db.PaymentOrders.AnyAsync(o => o.ChainId == 31337 && o.NetworkInstanceId != null, token) || await db.CryptoPayments.AnyAsync(p => p.ChainId == 31337 && p.NetworkInstanceId != null, token))
            throw new InvalidOperationException("Existing local instance metadata requires explicit review; automatic reassignment is forbidden.");
        var active = Eligible(source, plan.CutoffUtc).Single();
        var payment = source.Payments.Single(p => p.PaymentOrderId == active.PaymentOrderId);
        db.LocalTransitionBatches.Add(new LocalTransitionBatch
        {
            Id = plan.TransitionId, BackupSha256 = plan.SourceBackupSha256.ToLowerInvariant(), SourceFingerprint = snapshot.SourceFingerprint,
            LegacyNetworkInstanceId = plan.LegacyNetworkInstanceId, TargetNetworkInstanceId = plan.TargetNetworkInstanceId,
            ApprovalReference = plan.ApprovalReference, CutoffUtc = plan.CutoffUtc, SourceProjectionJson = JsonSerializer.Serialize(snapshot, JsonOptions)
        });
        db.LocalNetworkInstances.AddRange(new LocalNetworkInstance { Id = plan.LegacyNetworkInstanceId, Phase = "ArchivedUnverified", IsCurrent = false, TransitionBatchId = plan.TransitionId },
            new LocalNetworkInstance { Id = plan.TargetNetworkInstanceId, Phase = "Planned", IsCurrent = true, TransitionBatchId = plan.TransitionId });
        foreach (var order in await db.PaymentOrders.Where(o => o.ChainId == 31337).ToArrayAsync(token)) order.NetworkInstanceId = plan.LegacyNetworkInstanceId;
        foreach (var oldPayment in await db.CryptoPayments.Where(p => p.ChainId == 31337).ToArrayAsync(token)) oldPayment.NetworkInstanceId = plan.LegacyNetworkInstanceId;
        db.LegacyEntitlementProvenances.Add(new LegacyEntitlementProvenance
        {
            UserSubscriptionId = active.Id, SourcePaymentId = payment.Id, LegacyNetworkInstanceId = plan.LegacyNetworkInstanceId,
            TransitionBatchId = plan.TransitionId, OriginalStartDate = active.StartDate, OriginalEndDate = active.EndDate,
            OriginalAnalysisLimit = active.AnalysisLimit, OriginalAnalysesUsed = active.AnalysesUsed, OriginalFeatures = active.Features
        });
        await db.SaveChangesAsync(token);
        var after = await InspectAsync(plan.CutoffUtc, token);
        if (after.SourceFingerprint != snapshot.SourceFingerprint) throw new InvalidOperationException("A protected source field changed during transition.");
        if (tx is not null) await tx.CommitAsync(token);
        return after;
    }

    public async Task BindAsync(CancellationToken token)
    {
        var config = settings.Value.ResolveContracts();
        if (config.ChainId != 31337) throw new InvalidOperationException("Binding is restricted to a local test network.");
        var deployment = LocalDeployment.Read(config);
        var network = await db.LocalNetworkInstances.SingleOrDefaultAsync(n => n.Id == deployment.NetworkInstanceId && n.IsCurrent, token) ?? throw new InvalidOperationException("The deployment does not match the approved current instance.");
        if (network.Phase is not ("Planned" or "Deployed")) throw new InvalidOperationException("The target instance cannot be bound.");
        if (await db.PaymentOrders.AnyAsync(o => o.ChainId == 31337 && o.NetworkInstanceId == null, token) || await db.CryptoPayments.AnyAsync(p => p.ChainId == 31337 && p.NetworkInstanceId == null, token)) throw new InvalidOperationException("Unassigned local history blocks binding.");
        if (network.TransitionBatchId is { } batchId)
        {
            var batch = await db.LocalTransitionBatches.AsNoTracking().SingleAsync(b => b.Id == batchId, token);
            if (deployment.TransitionId != batch.Id || deployment.SourceBackupSha256 != batch.BackupSha256 || deployment.SourceFingerprint != batch.SourceFingerprint) throw new InvalidOperationException("The deployment does not preserve the explicitly approved transition provenance.");
        }
        var canonical = JsonSerializer.Serialize(deployment);
        if (network.DeploymentJson is not null && network.DeploymentJson != canonical) throw new InvalidOperationException("A bound instance cannot be replaced by another deployment.");
        foreach (var plan in await db.SubscriptionPlans.AsNoTracking().Where(p => p.IsActive && p.Price > 0).ToArrayAsync(token)) await verifier.CheckConfigurationAsync(plan.Id, plan.Price, token);
        network.DeploymentJson = canonical;
        network.Phase = "Deployed";
        await db.SaveChangesAsync(token);
        await LocalBlockchainGuard.WriteAsync(db, config.LocalDeploymentPath, token);
    }
}
