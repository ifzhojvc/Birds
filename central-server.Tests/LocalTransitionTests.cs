using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Models;
using BirdLab.Api.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class LocalTransitionTests
{
    internal sealed class Fixture : IAsyncDisposable
    {
        public BirdDbContext Db { get; }
        public FakeBlockchain Chain { get; } = new();
        public BlockchainOptions Config { get; } = SubscriptionTests.Options();
        public DateTimeOffset Cutoff { get; } = DateTimeOffset.UtcNow.AddMinutes(-1);
        public LocalTransitionService Service => new(Db, Chain, Options.Create(Config));
        public UserSubscription Pro { get; private set; } = null!;
        public UserSubscription Premium { get; private set; } = null!;
        public Fixture(BirdDbContext db) => Db = db;
        public static async Task<Fixture> Create(bool realSql = false)
        {
            var db = realSql ? throw new InvalidOperationException() : new BirdDbContext(new DbContextOptionsBuilder<BirdDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var result = new Fixture(db);
            await result.Populate();
            return result;
        }
        public async Task Populate()
        {
            var users = new[] { "transition-user-a", "transition-user-b" };
            Db.Users.AddRange(users.Select(id => new ApplicationUser { Id = id, UserName = id }));
            for (var index = 0; index < 2; index++)
            {
                var plan = await Db.SubscriptionPlans.AsNoTracking().SingleAsync(p => p.Id == index + 1);
                var wallet = "0x" + new string(index == 0 ? '3' : '4', 40);
                var order = new PaymentOrder
                {
                    UserId = users[index], PlanId = plan.Id, OrderId = wallet + new string(index == 0 ? 'a' : 'b', 24), WalletAddress = wallet,
                    ChainId = 31337, TokenContractAddress = Config.TokenAddress, PaymentContractAddress = Config.PaymentContractAddress,
                    Amount = plan.Price, TokenDecimals = 6, DurationDays = 30, AnalysisLimit = plan.AnalysisLimit, Features = plan.Features,
                    Status = index == 0 ? "Paid" : "Reorged", CreatedAt = Cutoff.AddDays(-1), ExpiresAt = Cutoff.AddDays(-1).AddMinutes(30)
                };
                var subscription = new UserSubscription
                {
                    UserId = users[index], PlanId = plan.Id, PaymentOrderId = order.Id, StartDate = Cutoff.AddDays(-1), EndDate = Cutoff.AddDays(29),
                    AnalysisLimit = plan.AnalysisLimit, AnalysesUsed = index == 0 ? 1 : 6, Features = plan.Features, Status = index == 0 ? "Active" : "Reorged"
                };
                Db.PaymentOrders.Add(order);
                Db.CryptoPayments.Add(new CryptoPayment
                {
                    PaymentOrderId = order.Id, UserId = order.UserId, SubscriptionPlanId = order.PlanId, OrderId = order.OrderId, WalletAddress = wallet,
                    BlockchainNetwork = "Local Anvil", ChainId = 31337, TokenContractAddress = order.TokenContractAddress, PaymentContractAddress = order.PaymentContractAddress,
                    Amount = order.Amount, TransactionHash = "0x" + new string(index == 0 ? 'a' : 'b', 64), BlockNumber = index + 7,
                    BlockHash = "0x" + new string(index == 0 ? 'c' : 'd', 64), LogIndex = 0, Status = index == 0 ? "Confirmed" : "Reorged", ConfirmedAt = Cutoff.AddDays(-1)
                });
                Db.UserSubscriptions.Add(subscription);
                Db.UserWallets.Add(new UserWallet { UserId = order.UserId, WalletAddress = wallet, IsVerified = true });
                for (var used = 0; used < subscription.AnalysesUsed; used++) Db.UsageRecords.Add(new UsageRecord { JobId = Guid.NewGuid(), UserId = order.UserId, UserSubscriptionId = subscription.Id, Status = "Consumed", Feature = "image" });
                if (index == 0) Pro = subscription; else Premium = subscription;
            }
            for (var index = 0; index < 36; index++) Db.Analyses.Add(new Analysis
            {
                UserId = users[index % 2], AnalysisType = index < 31 || index == 35 ? "image" : "audio", Status = index == 35 ? "Failed" : "Completed",
                StoredFilePath = Guid.NewGuid().ToString("N") + ".jpg", PredictedSpecies = index == 35 ? "" : "fixture"
            });
            await Db.SaveChangesAsync();
            foreach (var analysis in await Db.Analyses.Where(a => a.Status == "Completed").ToArrayAsync())
                for (var rank = 1; rank <= 3; rank++) Db.Predictions.Add(new Prediction { AnalysisId = analysis.Id, Rank = rank, SpeciesName = "species-" + rank, Confidence = .1 });
            await Db.SaveChangesAsync();
        }
        public async Task<LocalTransitionPlan> Plan()
        {
            var snapshot = await Service.InspectAsync(Cutoff, default);
            return new LocalTransitionPlan
            {
                TransitionId = Guid.NewGuid(), LegacyNetworkInstanceId = Guid.NewGuid(), TargetNetworkInstanceId = Guid.NewGuid(),
                SourceBackupSha256 = new string('8', 64), SourceFingerprint = snapshot.SourceFingerprint, ApprovalReference = "Explicit isolated transition test approval",
                CutoffUtc = Cutoff, ExpectedCounts = snapshot.Counts, ExpectedEntitlement = snapshot.EligibleEntitlement
            };
        }
        public LocalDeployment Deployment(LocalTransitionPlan plan) => new()
        {
            Version = 1, NetworkInstanceId = plan.TargetNetworkInstanceId, TransitionId = plan.TransitionId,
            SourceBackupSha256 = plan.SourceBackupSha256, SourceFingerprint = plan.SourceFingerprint,
            TestOnly = true, ChainId = 31337, TokenAddress = Config.TokenAddress, PaymentContractAddress = Config.PaymentContractAddress,
            OwnerAddress = "0x" + new string('3', 40), TokenDecimals = 6,
            TokenDeploymentTransactionHash = "0x" + new string('1', 64), DeploymentTransactionHash = "0x" + new string('2', 64),
            DeploymentBlockNumber = 2, DeploymentBlockHash = "0x" + new string('3', 64),
            NetworkIdentityTransactionHash = "0x" + new string('4', 64), NetworkIdentityData = "0x" + new string('5', 64),
            CheckpointBlockNumber = 3, CheckpointBlockHash = "0x" + new string('6', 64),
            TokenRuntimeCodeHash = LocalDeployment.CodeHash("0x1234"), PaymentRuntimeCodeHash = LocalDeployment.CodeHash("0x1234"),
            Plans = new() { [1] = 5_000_000, [2] = 10_000_000 }
        };
        public async Task Bind(LocalTransitionPlan plan, TestFiles files, Action<LocalDeployment>? change = null)
        {
            Config.NetworkInstanceId = plan.TargetNetworkInstanceId;
            Config.LocalDeploymentPath = Path.Combine(files.Folder, "deployment.json");
            var deployment = Deployment(plan);
            change?.Invoke(deployment);
            await File.WriteAllTextAsync(Config.LocalDeploymentPath, JsonSerializer.Serialize(deployment));
            await Service.BindAsync(default);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task PrepareIsAdditiveIdempotentAndPreservesExactlyTheExistingPro()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        var before = await fixture.Service.InspectAsync(fixture.Cutoff, default);
        Assert.Empty(before.Problems);
        Assert.Equal(36, before.Counts["analyses"]);
        Assert.Equal(105, before.Counts["predictions"]);
        Assert.Equal(7, before.Counts["consumedUsage"]);
        Assert.Equal(1, before.EligibleEntitlement!.AnalysesUsed);
        var after = await fixture.Service.PrepareAsync(plan, default);
        await fixture.Service.PrepareAsync(plan, default);
        Assert.Equal(before.SourceFingerprint, after.SourceFingerprint);
        Assert.Equal(2, await fixture.Db.LocalNetworkInstances.CountAsync());
        Assert.Single(fixture.Db.LocalTransitionBatches);
        var provenance = await fixture.Db.LegacyEntitlementProvenances.SingleAsync();
        Assert.Equal(fixture.Pro.Id, provenance.UserSubscriptionId);
        Assert.Equal(fixture.Pro.EndDate, provenance.OriginalEndDate);
        Assert.Equal(100, provenance.OriginalAnalysisLimit);
        Assert.Equal(1, provenance.OriginalAnalysesUsed);
        var entitlement = await new SubscriptionService(fixture.Db).GetAsync(fixture.Pro.UserId, default);
        Assert.Equal("PRO", entitlement.Plan);
        Assert.Equal("LegacyLocalSnapshot", entitlement.AccessSource);
        Assert.Equal("ArchivedUnverified", entitlement.VerificationState);
        Assert.Equal(99, entitlement.Remaining);
        Assert.Equal(plan.LegacyNetworkInstanceId, entitlement.NetworkInstanceId);
        Assert.Equal("Free", (await new SubscriptionService(fixture.Db).GetAsync(fixture.Premium.UserId, default)).Status);
        Assert.Equal("Reorged", fixture.Premium.Status);
        Assert.All(await fixture.Db.PaymentOrders.ToArrayAsync(), o => Assert.Equal(plan.LegacyNetworkInstanceId, o.NetworkInstanceId));
        Assert.All(await fixture.Db.CryptoPayments.ToArrayAsync(), p => Assert.Equal(plan.LegacyNetworkInstanceId, p.NetworkInstanceId));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("transition-id")]
    [InlineData("legacy-id")]
    [InlineData("target-id")]
    [InlineData("same-id")]
    [InlineData("backup")]
    [InlineData("fingerprint")]
    [InlineData("approval")]
    [InlineData("cutoff")]
    [InlineData("future-cutoff")]
    [InlineData("counts")]
    [InlineData("entitlement")]
    [InlineData("wrong-count")]
    [InlineData("wrong-entitlement")]
    public async Task IncompleteOrDifferentApprovalCannotChangeMetadata(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        switch (scenario)
        {
            case "version": plan.Version = 2; break;
            case "transition-id": plan.TransitionId = Guid.Empty; break;
            case "legacy-id": plan.LegacyNetworkInstanceId = Guid.Empty; break;
            case "target-id": plan.TargetNetworkInstanceId = Guid.Empty; break;
            case "same-id": plan.TargetNetworkInstanceId = plan.LegacyNetworkInstanceId; break;
            case "backup": plan.SourceBackupSha256 = "invalid"; break;
            case "fingerprint": plan.SourceFingerprint = new string('0', 64); break;
            case "approval": plan.ApprovalReference = ""; break;
            case "cutoff": plan.CutoffUtc = default; break;
            case "future-cutoff": plan.CutoffUtc = DateTimeOffset.UtcNow.AddDays(1); break;
            case "counts": plan.ExpectedCounts.Clear(); break;
            case "entitlement": plan.ExpectedEntitlement = null; break;
            case "wrong-count": plan.ExpectedCounts["analyses"]++; break;
            case "wrong-entitlement": plan.ExpectedEntitlement = plan.ExpectedEntitlement! with { AnalysesUsed = 0 }; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(plan, default));
        Assert.Empty(fixture.Db.LocalNetworkInstances);
        Assert.Empty(fixture.Db.LegacyEntitlementProvenances);
        Assert.All(fixture.Db.CryptoPayments, p => Assert.Null(p.NetworkInstanceId));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("payment")]
    [InlineData("subscription")]
    [InlineData("usage")]
    [InlineData("reserved")]
    [InlineData("unknown-usage")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("invalid-quota")]
    public async Task UnexpectedSourceDataStopsTheTransition(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        switch (scenario)
        {
            case "owner": (await fixture.Db.PaymentOrders.FirstAsync()).UserId = "orphan"; break;
            case "payment": (await fixture.Db.CryptoPayments.FirstAsync()).Amount++; break;
            case "subscription": fixture.Pro.PlanId = 2; break;
            case "usage": fixture.Pro.AnalysesUsed++; break;
            case "reserved": (await fixture.Db.UsageRecords.FirstAsync()).Status = "Reserved"; break;
            case "unknown-usage": (await fixture.Db.UsageRecords.FirstAsync()).Status = "Unknown"; break;
            case "future": fixture.Premium.Status = "Scheduled"; fixture.Premium.StartDate = fixture.Cutoff.AddDays(1); break;
            case "duplicate":
                var payment = await fixture.Db.CryptoPayments.FirstAsync();
                fixture.Db.CryptoPayments.Add(new CryptoPayment { PaymentOrderId = payment.PaymentOrderId });
                break;
            case "overlap":
                fixture.Db.UserSubscriptions.Add(new UserSubscription { UserId = fixture.Pro.UserId, PlanId = fixture.Pro.PlanId, PaymentOrderId = fixture.Pro.PaymentOrderId, StartDate = fixture.Pro.StartDate, EndDate = fixture.Pro.EndDate });
                break;
            case "invalid-quota": fixture.Pro.AnalysesUsed = 101; break;
        }
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(plan, default));
        Assert.Empty(fixture.Db.LocalTransitionBatches);
        Assert.Empty(fixture.Db.LegacyEntitlementProvenances);
    }

    [Fact]
    public async Task GuardArchivesEvidenceWithoutCheckingItAgainstTheNewChain()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        using var files = new TestFiles();
        var manifestPath = Path.Combine(files.Folder, "deployment.json");
        await LocalBlockchainGuard.WriteAsync(fixture.Db, manifestPath, default, plan.TargetNetworkInstanceId);
        using (var guard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.Folder, "sql-guard.json"))))
        {
            Assert.Equal(2, guard.RootElement.GetProperty("version").GetInt32());
            Assert.True(guard.RootElement.GetProperty("hasLocalPaymentHistory").GetBoolean());
            var active = guard.RootElement.GetProperty("activeNetwork");
            Assert.Equal("Planned", active.GetProperty("phase").GetString());
            Assert.Equal(plan.TargetNetworkInstanceId, active.GetProperty("networkInstanceId").GetGuid());
            Assert.Equal(plan.SourceBackupSha256, active.GetProperty("authorization").GetProperty("backupSha256").GetString());
            Assert.Equal(0, active.GetProperty("ordersAddresses").GetArrayLength());
            Assert.Equal(2, guard.RootElement.GetProperty("archivedNetworks")[0].GetProperty("confirmedPayments").GetArrayLength());
        }
        await fixture.Bind(plan, files);
        await fixture.Service.BindAsync(default);
        using (var deployedGuard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(files.Folder, "sql-guard.json"))))
        {
            var deployment = deployedGuard.RootElement.GetProperty("activeNetwork").GetProperty("deployment");
            Assert.Equal(plan.TargetNetworkInstanceId, deployment.GetProperty("networkInstanceId").GetGuid());
            Assert.Equal(1, deployment.GetProperty("version").GetInt32());
            Assert.True(deployment.TryGetProperty("networkIdentityTransactionHash", out _));
            Assert.False(deployment.TryGetProperty("NetworkInstanceId", out _));
        }
        var readiness = await new PaymentService(fixture.Db, fixture.Chain, Options.Create(fixture.Config)).ReadinessAsync(default);
        Assert.True(readiness.Ready);
        var current = await LocalNetworkPolicy.RequireCurrentAsync(fixture.Db, fixture.Config, default);
        Assert.Equal(plan.TargetNetworkInstanceId, current!.Id);
        Assert.Equal("Deployed", current.Phase);
        Assert.Equal(plan.SourceFingerprint, (await fixture.Service.InspectAsync(fixture.Cutoff, default)).SourceFingerprint);
    }

    [Fact]
    public async Task ArchivedApiRetriesAndOldHashesCannotMutatePaymentsOrGrantPremium()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        using var files = new TestFiles();
        await fixture.Bind(plan, files);
        var service = new PaymentService(fixture.Db, fixture.Chain, Options.Create(fixture.Config));
        var original = await fixture.Service.InspectAsync(fixture.Cutoff, default);
        var oldPayments = await fixture.Db.CryptoPayments.AsNoTracking().ToArrayAsync();
        fixture.Chain.Evidence = new("Confirmed");
        foreach (var payment in oldPayments)
        {
            var result = await service.ProcessAsync(payment.Id, default);
            Assert.Equal(payment.Status, result.Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(payment.UserId, payment.PaymentOrderId, "0x" + new string('f', 64), default))).Status);
        }
        Assert.Equal(original.SourceFingerprint, (await fixture.Service.InspectAsync(fixture.Cutoff, default)).SourceFingerprint);
        var wallet = (await fixture.Db.UserWallets.SingleAsync(w => w.UserId == fixture.Pro.UserId)).WalletAddress;
        var newOrder = await service.CreateAsync(fixture.Pro.UserId, 1, wallet, false, default);
        foreach (var oldPayment in oldPayments)
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => service.SubmitAsync(fixture.Pro.UserId, newOrder.Id, oldPayment.TransactionHash, default));
            Assert.Equal(409, error.Status);
            Assert.Equal("historical_transaction_replay", error.Code);
        }
        Assert.Equal(2, await fixture.Db.CryptoPayments.CountAsync());
        Assert.Equal(2, await fixture.Db.UserSubscriptions.CountAsync());
        Assert.Equal("Free", (await new SubscriptionService(fixture.Db).GetAsync(fixture.Premium.UserId, default)).Status);
        var access = await new SubscriptionService(fixture.Db).GetAsync(fixture.Pro.UserId, default);
        Assert.Equal(99, access.Remaining);
        Assert.Equal(fixture.Pro.EndDate, access.EndDate);
        var subscriptions = new SubscriptionService(fixture.Db);
        var job = Guid.NewGuid();
        await subscriptions.ReserveAsync(fixture.Pro.UserId, job, "image", default);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(98, (await subscriptions.GetAsync(fixture.Pro.UserId, default)).Remaining);
        await SubscriptionService.SettleAsync(fixture.Db, job, false, default);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(99, (await subscriptions.GetAsync(fixture.Pro.UserId, default)).Remaining);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("source")]
    [InlineData("transition")]
    [InlineData("network")]
    [InlineData("rpc")]
    public async Task BindingRequiresTheApprovedInstanceAndProvenance(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        using var files = new TestFiles();
        if (scenario == "rpc") fixture.Chain.ConfigurationError = new IOException();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Bind(plan, files, d =>
        {
            if (scenario == "backup") d.SourceBackupSha256 = new string('0', 64);
            if (scenario == "source") d.SourceFingerprint = new string('0', 64);
            if (scenario == "transition") d.TransitionId = Guid.NewGuid();
            if (scenario == "network") d.NetworkInstanceId = Guid.NewGuid();
        }));
        Assert.Equal("Planned", (await fixture.Db.LocalNetworkInstances.SingleAsync(n => n.IsCurrent)).Phase);
        Assert.Null((await fixture.Db.LocalNetworkInstances.SingleAsync(n => n.IsCurrent)).DeploymentJson);
    }

    [Fact]
    public async Task LegacyTermTamperingStopsAccessWithoutExtendingOrResettingIt()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        fixture.Pro.EndDate = fixture.Pro.EndDate.AddDays(1);
        await fixture.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => new SubscriptionService(fixture.Db).GetAsync(fixture.Pro.UserId, default));
        Assert.Equal("legacy_entitlement_mismatch", error.Code);
        Assert.Equal(1, fixture.Pro.AnalysesUsed);
        Assert.Equal(1, await fixture.Db.LegacyEntitlementProvenances.CountAsync());
    }

    [Fact]
    public async Task FreshSqlGetsOnePlannedIdentityAndCannotReplaceLostMetadata()
    {
        await using var db = new BirdDbContext(new DbContextOptionsBuilder<BirdDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.Database.EnsureCreatedAsync();
        using var files = new TestFiles();
        var manifestPath = Path.Combine(files.Folder, "deployment.json");
        await LocalBlockchainGuard.WriteAsync(db, manifestPath, default);
        var instance = await db.LocalNetworkInstances.SingleAsync();
        Assert.Equal("Planned", instance.Phase);
        await LocalBlockchainGuard.WriteAsync(db, manifestPath, default);
        Assert.Equal(instance.Id, (await db.LocalNetworkInstances.SingleAsync()).Id);
        db.LocalNetworkInstances.Remove(instance);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBlockchainGuard.WriteAsync(db, manifestPath, default));
    }
    [SqlFact]
    public async Task RealSqlTransitionAndMetadataRollbackPreserveEveryProtectedSourceField()
    {
        await using var database = new SqlDatabase();
        var db = database.Db;
        await db.Database.MigrateAsync();
        var fixture = new Fixture(db);
        await fixture.Populate();
        var plan = await fixture.Plan();
        var before = await fixture.Service.InspectAsync(fixture.Cutoff, default);
        await fixture.Service.PrepareAsync(plan, default);
        db.ChangeTracker.Clear();
        await fixture.Service.PrepareAsync(plan, default);
        Assert.Equal(before.SourceFingerprint, (await fixture.Service.InspectAsync(fixture.Cutoff, default)).SourceFingerprint);
        Assert.Equal(1, await db.LegacyEntitlementProvenances.CountAsync());
        Assert.Equal(99, (await new SubscriptionService(db).GetAsync(fixture.Pro.UserId, default)).Remaining);
        Assert.Equal("Free", (await new SubscriptionService(db).GetAsync(fixture.Premium.UserId, default)).Status);
        await db.GetService<IMigrator>().MigrateAsync("20261103154944_CryptoSubscriptions");
        db.ChangeTracker.Clear();
        Assert.Equal(before.SourceFingerprint, (await fixture.Service.InspectAsync(fixture.Cutoff, default)).SourceFingerprint);
        await db.Database.MigrateAsync();
        Assert.Empty(db.LocalNetworkInstances);
        Assert.Equal(before.SourceFingerprint, (await fixture.Service.InspectAsync(fixture.Cutoff, default)).SourceFingerprint);
        await fixture.Service.PrepareAsync(plan, default);
        Assert.Equal(99, (await new SubscriptionService(db).GetAsync(fixture.Pro.UserId, default)).Remaining);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("network-metadata")]
    [InlineData("missing-provenance")]
    public async Task IdempotentPreparationRejectsChangedAuthorizationOrMetadata(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        if (scenario == "approval") plan.ApprovalReference += " changed";
        if (scenario == "network-metadata") (await fixture.Db.PaymentOrders.FirstAsync()).NetworkInstanceId = plan.TargetNetworkInstanceId;
        if (scenario == "missing-provenance") fixture.Db.LegacyEntitlementProvenances.Remove(await fixture.Db.LegacyEntitlementProvenances.SingleAsync());
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(plan, default));
        Assert.Equal(2, await fixture.Db.CryptoPayments.CountAsync());
        Assert.Equal(1, fixture.Pro.AnalysesUsed);
    }

    [Fact]
    public async Task ExistingInstanceCannotBeSilentlyReassignedByAnotherApproval()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        fixture.Db.LocalNetworkInstances.Add(new LocalNetworkInstance { IsCurrent = true, Phase = "Planned" });
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareAsync(plan, default));
        Assert.Empty(fixture.Db.LocalTransitionBatches);
        Assert.Empty(fixture.Db.LegacyEntitlementProvenances);
    }

    [Theory]
    [InlineData("payment")]
    [InlineData("network")]
    [InlineData("batch")]
    [InlineData("policy")]
    [InlineData("source")]
    public async Task LegacyAccessRequiresTheCorrectApprovedPaymentProvenance(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        var provenance = await fixture.Db.LegacyEntitlementProvenances.SingleAsync();
        if (scenario == "payment") provenance.SourcePaymentId = (await fixture.Db.CryptoPayments.SingleAsync(p => p.Status == "Reorged")).Id;
        if (scenario == "network") provenance.LegacyNetworkInstanceId = plan.TargetNetworkInstanceId;
        if (scenario == "batch") provenance.TransitionBatchId = Guid.NewGuid();
        if (scenario == "policy") provenance.Policy = "Unapproved";
        if (scenario == "source") provenance.AccessSource = "Unapproved";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal("legacy_entitlement_mismatch", (await Assert.ThrowsAsync<ApiException>(() => new SubscriptionService(fixture.Db).GetAsync(fixture.Pro.UserId, default))).Code);
        Assert.Equal(100, fixture.Pro.AnalysisLimit);
        Assert.Equal(1, fixture.Pro.AnalysesUsed);
    }

    [Fact]
    public async Task GuardUpgradesLegacyEvidenceAndNeverDowngradesVersionedMetadata()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        using var files = new TestFiles();
        var deploymentPath = Path.Combine(files.Folder, "deployment.json");
        var guardPath = Path.Combine(files.Folder, "sql-guard.json");
        var old = new LocalSqlGuard
        {
            HasLocalPaymentHistory = true,
            OrdersAddresses = [new(fixture.Config.TokenAddress, fixture.Config.PaymentContractAddress)],
            ConfirmedPayments = (await fixture.Db.CryptoPayments.ToArrayAsync()).Select(p => new LocalConfirmedPayment(p.TransactionHash, p.BlockNumber, p.BlockHash, p.TokenContractAddress, p.PaymentContractAddress)).ToArray()
        };
        await File.WriteAllTextAsync(guardPath, JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await LocalBlockchainGuard.WriteAsync(fixture.Db, deploymentPath, default);
        var saved = await File.ReadAllTextAsync(guardPath);
        fixture.Db.LegacyEntitlementProvenances.RemoveRange(await fixture.Db.LegacyEntitlementProvenances.ToArrayAsync());
        fixture.Db.LocalNetworkInstances.RemoveRange(await fixture.Db.LocalNetworkInstances.ToArrayAsync());
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBlockchainGuard.WriteAsync(fixture.Db, deploymentPath, default));
        Assert.Equal(saved, await File.ReadAllTextAsync(guardPath));
        await File.WriteAllTextAsync(guardPath, "{\"version\":3,\"hasLocalPaymentHistory\":true}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBlockchainGuard.WriteAsync(fixture.Db, deploymentPath, default));
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("mismatched-order")]
    [InlineData("manifest")]
    public async Task NewPaymentReadinessFailsClosedWithoutRevokingLegacyPro(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Plan();
        await fixture.Service.PrepareAsync(plan, default);
        using var files = new TestFiles();
        await fixture.Bind(plan, files);
        if (scenario == "unassigned") (await fixture.Db.PaymentOrders.SingleAsync(o => o.Id == fixture.Premium.PaymentOrderId)).NetworkInstanceId = null;
        if (scenario == "mismatched-order") (await fixture.Db.CryptoPayments.SingleAsync(p => p.Status == "Reorged")).NetworkInstanceId = plan.TargetNetworkInstanceId;
        if (scenario == "manifest")
        {
            var deployment = fixture.Deployment(plan);
            deployment.NetworkIdentityData = "0x" + new string('f', 64);
            await File.WriteAllTextAsync(fixture.Config.LocalDeploymentPath, JsonSerializer.Serialize(deployment));
        }
        await fixture.Db.SaveChangesAsync();
        Assert.False((await new PaymentService(fixture.Db, fixture.Chain, Options.Create(fixture.Config)).ReadinessAsync(default)).Ready);
        Assert.Equal(99, (await new SubscriptionService(fixture.Db).GetAsync(fixture.Pro.UserId, default)).Remaining);
    }

}
