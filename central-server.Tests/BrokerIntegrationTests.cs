using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Messaging;
using BirdLab.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class BrokerFactAttribute : FactAttribute
{
    public BrokerFactAttribute()
    {
        if (new[] { "BIRDLAB_TEST_SQL", "BIRDLAB_TEST_AMQP", "BIRDLAB_TEST_RABBIT_HTTP" }
            .Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
            Skip = "Потрібні окремі SQL Server і RabbitMQ: BIRDLAB_TEST_SQL, BIRDLAB_TEST_AMQP, BIRDLAB_TEST_RABBIT_HTTP.";
    }
}

public sealed class BrokerIntegrationTests
{
    private sealed class RejectFirstSave : SaveChangesInterceptor
    {
        private int attempted;
        public int Attempts => Volatile.Read(ref attempted);
        public volatile bool RejectAll;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref attempted) == 1 || RejectAll)
                data.Context!.ChangeTracker.Entries<InboxMessage>().Single(row => row.State == EntityState.Added).Entity.BodyHash = new string('x', 65);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition()) await Task.Delay(75, token);
    }

    [BrokerFact]
    public async Task RealBrokerRedeliversRetriesDeadLettersAndRecoversOutboxWithoutDuplicateWrites()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        var amqp = new Uri(Environment.GetEnvironmentVariable("BIRDLAB_TEST_AMQP")!);
        var credentials = amqp.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        var vhost = "birdlab-coverage-" + Guid.NewGuid().ToString("N");
        using var admin = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("BIRDLAB_TEST_RABBIT_HTTP")!) };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(':', credentials))));
        async Task Put(string path, object body)
        {
            using var response = await admin.PutAsJsonAsync(path, body, token);
            response.EnsureSuccessStatusCode();
        }
        await Put($"/api/vhosts/{vhost}", new { });
        try
        {
            await Put($"/api/permissions/{vhost}/{Uri.EscapeDataString(credentials[0])}", new { configure = ".*", write = ".*", read = ".*" });
            await Put($"/api/policies/{vhost}/test-dlq", new Dictionary<string, object>
            {
                ["pattern"] = "^bird\\.analysis\\.(requests|results)$", ["apply-to"] = "quorum_queues",
                ["definition"] = new Dictionary<string, object> { ["dead-letter-exchange"] = "bird.analysis.dlx", ["dead-letter-routing-key"] = "analysis.dead" },
            });
            await Put($"/api/policies/{vhost}/test-retry", new Dictionary<string, object>
            {
                ["pattern"] = "^bird\\.analysis\\.results\\.retry\\.1$", ["apply-to"] = "quorum_queues",
                ["definition"] = new Dictionary<string, object> { ["message-ttl"] = 150, ["dead-letter-exchange"] = RabbitBus.Exchange, ["dead-letter-routing-key"] = "analysis.event" },
            });
            await using var database = new SqlDatabase();
            var db = database.Db;
            await db.Database.MigrateAsync(token);
            using var media = new TestFiles();
            var service = await TestAccount.Analyses(db, new UnitAiClient(true), media.Store);
            var job = await service.EnqueueAsync(new FormFile(new MemoryStream([1]), 0, 1, "file", "bird.jpg"), "image", token);
            var failingCommit = new RejectFirstSave();
            var connection = db.Database.GetConnectionString();
            await using var provider = new ServiceCollection()
                .AddDbContext<BirdDbContext>(options => options.UseSqlServer(connection).AddInterceptors(failingCommit))
                .AddScoped<ResultProcessor>().BuildServiceProvider();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMQ:Host"] = amqp.Host, ["RabbitMQ:Port"] = "1", ["RabbitMQ:VirtualHost"] = vhost,
                ["RabbitMQ:Username"] = credentials[0], ["RabbitMQ:Password"] = credentials[1],
            }).Build();
            await using var bus = new RabbitBus(config, NullLogger<RabbitBus>.Instance);

            Assert.False(await bus.ProbeAsync(token));
            await using (var outboxProvider = new ServiceCollection().AddDbContext<BirdDbContext>(options => options.UseSqlServer(connection)).BuildServiceProvider())
            using (var outbox = new OutboxDispatcher(outboxProvider.GetRequiredService<IServiceScopeFactory>(), bus, NullLogger<OutboxDispatcher>.Instance))
            {
                Assert.True(await outbox.DispatchOneAsync(token));
                var queued = await db.OutboxMessages.AsNoTracking().SingleAsync(token);
                Assert.Equal(1, queued.Attempts);
                Assert.Null(queued.PublishedAt);
                Assert.NotNull(queued.LastError);
                Assert.Equal("Pending", (await db.Analyses.AsNoTracking().SingleAsync(token)).Status);

                config["RabbitMQ:Port"] = amqp.Port.ToString();
                await db.OutboxMessages.ExecuteUpdateAsync(row => row.SetProperty(e => e.NextAttemptAt, DateTimeOffset.UtcNow), token);
                Assert.True(await outbox.DispatchOneAsync(token));
            }
            var sent = await db.OutboxMessages.AsNoTracking().SingleAsync(token);
            Assert.Equal(2, sent.Attempts);
            Assert.NotNull(sent.PublishedAt);
            Assert.Null(sent.LastError);
            Assert.True(await bus.ProbeAsync(token));
            var initialConnection = await bus.ConnectAsync(token);
            await using (var channel = await initialConnection.CreateChannelAsync(cancellationToken: token))
            {
                var delivered = await channel.BasicGetAsync(RabbitBus.Requests, false, token);
                Assert.NotNull(delivered);
                Assert.Equal(sent.Id.ToString(), delivered.BasicProperties.MessageId);
                var requested = JsonSerializer.Deserialize<AnalysisRequest>(delivered.Body.Span, EventJson.Options)!;
                Assert.Equal(job.AnalysisId, requested.AnalysisId);
                await channel.BasicNackAsync(delivered.DeliveryTag, false, true, token);
                BasicGetResult? repeated = null;
                await WaitAsync(async () => (repeated = await channel.BasicGetAsync(RabbitBus.Requests, false, token)) is not null, token);
                Assert.True(repeated!.Redelivered);
                Assert.Equal(delivered.BasicProperties.MessageId, repeated.BasicProperties.MessageId);
                await channel.BasicAckAsync(repeated.DeliveryTag, false, token);
            }
            var analysis = await db.Analyses.AsNoTracking().SingleAsync(token);
            var complete = new AnalysisEvent(1, Guid.NewGuid(), analysis.JobId, analysis.CorrelationId, sent.Id, "completed", DateTimeOffset.UtcNow, 0,
                [new("fixture-first", null, .8, 1), new("fixture-second", null, .15, 2), new("fixture-third", null, .05, 3)],
                "fixture-first", .8, "broker-test-model", "broker-test-revision", .1, JsonSerializer.SerializeToElement(new { mock_used = false }));
            var body = JsonSerializer.SerializeToUtf8Bytes(complete, EventJson.Options);
            using var consumer = new ResultConsumer(bus, scopes, NullLogger<ResultConsumer>.Instance);
            await consumer.StartAsync(token);
            try
            {
                await WaitAsync(() => Task.FromResult(consumer.Listening), token);
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", body, complete.EventId, complete.CorrelationId, token);
                await WaitAsync(() => db.InboxMessages.AnyAsync(e => e.Id == complete.EventId, token), token);
                Assert.True(failingCommit.Attempts >= 2);
                await initialConnection.CloseAsync(token);
                await WaitAsync(() => Task.FromResult(!consumer.Listening), token);
                await WaitAsync(() => Task.FromResult(consumer.Listening), token);
                var recovered = await bus.ConnectAsync(token);
                Assert.NotSame(initialConnection, recovered);
                var rejected = await Assert.ThrowsAnyAsync<PublishException>(() => bus.PublishAsync(RabbitBus.Exchange, "invalid-routing-key", body, complete.EventId, complete.CorrelationId, token));
                Assert.True(rejected.IsReturn);
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", body, complete.EventId, complete.CorrelationId, token);
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", "not JSON"u8.ToArray(), Guid.NewGuid(), complete.CorrelationId, token);
                var unknown = complete with { SchemaVersion = 2, EventId = Guid.NewGuid() };
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", JsonSerializer.SerializeToUtf8Bytes(unknown, EventJson.Options), unknown.EventId, unknown.CorrelationId, token);
                failingCommit.RejectAll = true;
                var exhausted = complete with { EventId = Guid.NewGuid() };
                var invalidRetry = complete with { EventId = Guid.NewGuid() };
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", JsonSerializer.SerializeToUtf8Bytes(exhausted, EventJson.Options), exhausted.EventId, complete.CorrelationId, token, retry: 3);
                await bus.PublishAsync(RabbitBus.Exchange, "analysis.event", JsonSerializer.SerializeToUtf8Bytes(invalidRetry, EventJson.Options), invalidRetry.EventId, complete.CorrelationId, token, retry: 4);
                await using var channel = await recovered.CreateChannelAsync(cancellationToken: token);
                await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync("bird.analysis.dead", token)).MessageCount == 4, token);
                var firstDead = await channel.BasicGetAsync("bird.analysis.dead", true, token);
                var secondDead = await channel.BasicGetAsync("bird.analysis.dead", true, token);
                Assert.Equal("not JSON", Encoding.UTF8.GetString(firstDead!.Body.Span));
                Assert.Equal(unknown.EventId.ToString(), secondDead!.BasicProperties.MessageId);
                Assert.Equal(exhausted.EventId.ToString(), (await channel.BasicGetAsync("bird.analysis.dead", true, token))!.BasicProperties.MessageId);
                Assert.Equal(invalidRetry.EventId.ToString(), (await channel.BasicGetAsync("bird.analysis.dead", true, token))!.BasicProperties.MessageId);
                Assert.Equal(1, await db.InboxMessages.CountAsync(token));
                Assert.Equal(3, await db.Predictions.CountAsync(token));
                Assert.Equal("Consumed", (await db.UsageRecords.AsNoTracking().SingleAsync(token)).Status);
                Assert.Equal("Completed", (await db.Analyses.AsNoTracking().SingleAsync(token)).Status);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await consumer.StopAsync(stop.Token);
            }
        }
        finally
        {
            using var cleanup = await admin.DeleteAsync($"/api/vhosts/{vhost}");
            cleanup.EnsureSuccessStatusCode();
        }
    }
}
