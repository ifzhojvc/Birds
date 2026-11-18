using BirdLab.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirdLab.Api.Data;

public static class SubscriptionSchema
{
    public static void Configure(ModelBuilder model)
    {
        var session = model.Entity<AuthSession>();
        session.Property(s => s.Id).HasMaxLength(64);
        session.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        session.HasIndex(s => s.ExpiresAt);
        var plan = model.Entity<SubscriptionPlan>();
        plan.Property(p => p.Id).ValueGeneratedNever();
        plan.Property(p => p.Name).HasMaxLength(32);
        plan.Property(p => p.Currency).HasMaxLength(16);
        plan.HasIndex(p => p.Name).IsUnique();
        plan.ToTable(t => t.HasCheckConstraint("CK_Plan_Terms", "[Price] >= 0 AND [AnalysisLimit] > 0 AND [DurationDays] > 0"));
        plan.HasData(
            new SubscriptionPlan { Id = 0, Name = "FREE", Price = 0, DurationDays = 1, AnalysisLimit = 5, Features = "[\"image\",\"history\",\"basicInfo\"]" },
            new SubscriptionPlan { Id = 1, Name = "PRO", Price = 5_000_000, DurationDays = 30, AnalysisLimit = 100, Features = "[\"image\",\"audio\",\"history\",\"basicInfo\",\"gallery\",\"statistics\"]" },
            new SubscriptionPlan { Id = 2, Name = "PREMIUM", Price = 10_000_000, DurationDays = 30, AnalysisLimit = 500, Features = "[\"image\",\"audio\",\"history\",\"basicInfo\",\"gallery\",\"statistics\",\"batch\",\"compare\",\"advancedStatistics\"]" });
        var subscription = model.Entity<UserSubscription>();
        subscription.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        subscription.HasOne<SubscriptionPlan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
        subscription.HasOne<PaymentOrder>().WithMany().HasForeignKey(s => s.PaymentOrderId).OnDelete(DeleteBehavior.Restrict);
        subscription.HasIndex(s => s.PaymentOrderId).IsUnique();
        subscription.HasIndex(s => new { s.UserId, s.StartDate, s.EndDate });
        subscription.Property(s => s.Status).HasMaxLength(24);
        subscription.ToTable(t => t.HasCheckConstraint("CK_Subscription_Usage", "[AnalysesUsed] >= 0 AND [AnalysesUsed] <= [AnalysisLimit] AND [EndDate] > [StartDate]"));
        var order = model.Entity<PaymentOrder>();
        order.Property(p => p.OrderId).HasMaxLength(66);
        order.Property(p => p.WalletAddress).HasMaxLength(42);
        order.Property(p => p.TokenContractAddress).HasMaxLength(42);
        order.Property(p => p.PaymentContractAddress).HasMaxLength(42);
        order.Property(p => p.Status).HasMaxLength(24);
        order.HasIndex(p => p.OrderId).IsUnique();
        order.HasIndex(p => new { p.UserId, p.CreatedAt });
        order.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<SubscriptionPlan>().WithMany().HasForeignKey(p => p.PlanId).OnDelete(DeleteBehavior.Restrict);
        order.ToTable(t => t.HasCheckConstraint("CK_Order_Amount", "[Amount] > 0"));
        var payment = model.Entity<CryptoPayment>();
        payment.Property(p => p.OrderId).HasMaxLength(66);
        payment.Property(p => p.TransactionHash).HasMaxLength(66);
        payment.Property(p => p.BlockHash).HasMaxLength(66);
        payment.Property(p => p.WalletAddress).HasMaxLength(42);
        payment.Property(p => p.TokenContractAddress).HasMaxLength(42);
        payment.Property(p => p.PaymentContractAddress).HasMaxLength(42);
        payment.Property(p => p.BlockchainNetwork).HasMaxLength(32);
        payment.Property(p => p.Status).HasMaxLength(24);
        payment.Property(p => p.Error).HasMaxLength(500);
        payment.HasOne<PaymentOrder>().WithMany().HasForeignKey(p => p.PaymentOrderId).OnDelete(DeleteBehavior.Restrict);
        payment.HasIndex(p => p.OrderId).IsUnique();
        payment.HasIndex(p => new { p.ChainId, p.TransactionHash, p.LogIndex }).IsUnique().HasFilter("[LogIndex] IS NOT NULL AND [ChainId] <> 31337");
        payment.HasIndex(p => new { p.NetworkInstanceId, p.TransactionHash, p.LogIndex }).IsUnique().HasFilter("[LogIndex] IS NOT NULL AND [NetworkInstanceId] IS NOT NULL");
        order.HasOne<LocalNetworkInstance>().WithMany().HasForeignKey(o => o.NetworkInstanceId).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne<LocalNetworkInstance>().WithMany().HasForeignKey(p => p.NetworkInstanceId).OnDelete(DeleteBehavior.Restrict);
        payment.HasIndex(p => new { p.Status, p.NextCheckAt });
        var network = model.Entity<LocalNetworkInstance>();
        network.Property(n => n.Id).ValueGeneratedNever();
        network.Property(n => n.Phase).HasMaxLength(24);
        network.HasIndex(n => n.ChainId).IsUnique().HasFilter("[IsCurrent] = 1");
        network.HasOne<LocalTransitionBatch>().WithMany().HasForeignKey(n => n.TransitionBatchId).OnDelete(DeleteBehavior.Restrict);
        network.ToTable(t => t.HasCheckConstraint("CK_LocalNetwork_State", "[ChainId] = 31337 AND [Phase] IN ('ArchivedUnverified','Planned','Deployed') AND (([Phase] = 'ArchivedUnverified' AND [IsCurrent] = 0) OR ([Phase] IN ('Planned','Deployed') AND [IsCurrent] = 1))"));
        var transition = model.Entity<LocalTransitionBatch>();
        transition.Property(t => t.Id).ValueGeneratedNever();
        transition.Property(t => t.BackupSha256).HasMaxLength(64);
        transition.Property(t => t.SourceFingerprint).HasMaxLength(64);
        transition.Property(t => t.ApprovalReference).HasMaxLength(256);
        transition.HasIndex(t => t.TargetNetworkInstanceId).IsUnique();
        var provenance = model.Entity<LegacyEntitlementProvenance>();
        provenance.HasKey(p => p.UserSubscriptionId);
        provenance.Property(p => p.AccessSource).HasMaxLength(32);
        provenance.Property(p => p.Policy).HasMaxLength(48);
        provenance.HasOne<UserSubscription>().WithOne().HasForeignKey<LegacyEntitlementProvenance>(p => p.UserSubscriptionId).OnDelete(DeleteBehavior.Restrict);
        provenance.HasOne<CryptoPayment>().WithMany().HasForeignKey(p => p.SourcePaymentId).OnDelete(DeleteBehavior.Restrict);
        provenance.HasOne<LocalNetworkInstance>().WithMany().HasForeignKey(p => p.LegacyNetworkInstanceId).OnDelete(DeleteBehavior.Restrict);
        provenance.HasOne<LocalTransitionBatch>().WithMany().HasForeignKey(p => p.TransitionBatchId).OnDelete(DeleteBehavior.Restrict);
        var wallet = model.Entity<UserWallet>();
        wallet.Property(w => w.WalletAddress).HasMaxLength(42);
        wallet.HasIndex(w => w.WalletAddress).IsUnique();
        wallet.HasIndex(w => new { w.UserId, w.WalletAddress }).IsUnique();
        wallet.HasOne<ApplicationUser>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Restrict);
        var nonce = model.Entity<WalletNonce>();
        nonce.Property(n => n.Id).HasMaxLength(64);
        nonce.Property(n => n.WalletAddress).HasMaxLength(42);
        nonce.Property(n => n.Domain).HasMaxLength(256);
        nonce.HasOne<ApplicationUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        nonce.HasIndex(n => n.ExpiresAt);
        var usage = model.Entity<UsageRecord>();
        usage.HasIndex(u => u.JobId).IsUnique();
        usage.HasIndex(u => new { u.UserId, u.CreatedAt });
        usage.Property(u => u.Status).HasMaxLength(16);
        usage.Property(u => u.Feature).HasMaxLength(32);
        usage.HasOne<ApplicationUser>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Restrict);
        usage.HasOne<UserSubscription>().WithMany().HasForeignKey(u => u.UserSubscriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}
