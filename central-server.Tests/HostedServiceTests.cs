using BirdLab.Api.Data;
using BirdLab.Api.Messaging;
using BirdLab.Api.Models;
using BirdLab.Api.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class HostedServiceTests
{
    [SqlFact]
    public async Task SubscriptionMonitorExpiresOldPeriodAndSettlesDuePayment()
    {
        await using var database = new SqlDatabase();
        var db = database.Db;
        await db.Database.MigrateAsync();
        db.Users.Add(new ApplicationUser { Id = TestAccount.UserId, UserName = "monitor@example.test" });
        await db.SaveChangesAsync();
        var (payments, order, chain) = await SubscriptionTests.Order(db);
        var first = await payments.SubmitAsync(TestAccount.UserId, order.Id, "0x" + new string('a', 64), default);
        await payments.ProcessAsync(first.Id, default);
        var expired = await db.UserSubscriptions.SingleAsync();
        expired.StartDate = DateTimeOffset.UtcNow.AddDays(-31);
        expired.EndDate = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        var renewal = await payments.CreateAsync(TestAccount.UserId, order.PlanId, order.WalletAddress, false, default);
        var pending = await payments.SubmitAsync(TestAccount.UserId, renewal.Id, "0x" + new string('b', 64), default);
        Assert.Equal("Pending", pending.Status);
        var options = SubscriptionTests.Options();
        options.PollSeconds = 2;
        var settings = Options.Create(options);
        await using var services = new ServiceCollection()
            .AddDbContext<BirdDbContext>(options => options.UseSqlServer(db.Database.GetConnectionString()))
            .AddSingleton<IBlockchainVerifier>(chain)
            .AddSingleton<IOptions<BlockchainOptions>>(settings)
            .AddScoped<SubscriptionService>()
            .AddScoped<PaymentService>()
            .BuildServiceProvider();
        using var monitor = new SubscriptionMonitor(services.GetRequiredService<IServiceScopeFactory>(), settings, NullLogger<SubscriptionMonitor>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await monitor.StartAsync(timeout.Token);
        try
        {
            while (!await db.CryptoPayments.AsNoTracking().AnyAsync(row => row.Id == pending.Id && row.Status == "Confirmed", timeout.Token))
                await Task.Delay(100, timeout.Token);
            var periods = await db.UserSubscriptions.AsNoTracking().OrderBy(row => row.StartDate).ToArrayAsync(timeout.Token);
            Assert.Equal(new[] { "Expired", "Active" }, periods.Select(row => row.Status));
            Assert.Equal(renewal.Id, periods[1].PaymentOrderId);
        }
        finally { await monitor.StopAsync(CancellationToken.None); }
    }

    private sealed class WarningProbe : ILogger<StalledJobMonitor>
    {
        public TaskCompletionSource<string> FirstWarning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level == LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (IsEnabled(level)) FirstWarning.TrySetResult(format(state, exception));
        }
    }

    [SqlFact]
    public async Task StalledMonitorReportsOldProcessingJobAndPreservesItsState()
    {
        await using var database = new SqlDatabase();
        var db = database.Db;
        await db.Database.MigrateAsync();
        var stale = DateTimeOffset.UtcNow.AddMinutes(-20);
        var pending = new Analysis { AnalysisType = "image", Status = "Processing", UpdatedAt = stale, StoredFilePath = "processing.jpg" };
        var completed = new Analysis { AnalysisType = "image", Status = "Completed", UpdatedAt = stale, StoredFilePath = "complete.jpg" };
        db.Analyses.AddRange(pending, completed);
        await db.SaveChangesAsync();
        await using var services = new ServiceCollection().AddDbContext<BirdDbContext>(options => options.UseSqlServer(db.Database.GetConnectionString())).BuildServiceProvider();
        var log = new WarningProbe();
        using var monitor = new StalledJobMonitor(services.GetRequiredService<IServiceScopeFactory>(), log);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            var warning = await log.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(75));
            Assert.Contains(pending.JobId.ToString(), warning);
            Assert.Contains(pending.CorrelationId.ToString(), warning);
            Assert.DoesNotContain(completed.JobId.ToString(), warning);
            Assert.Equal("Processing", (await db.Analyses.AsNoTracking().SingleAsync(row => row.Id == pending.Id)).Status);
        }
        finally { await monitor.StopAsync(CancellationToken.None); }
    }
}
