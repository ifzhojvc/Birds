using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Models;
using BirdLab.Api.Services;
using BirdLab.Api.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class HttpWorkflowTests
{
    private static MultipartFormDataContent File(string name, string field = "file")
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent([1, 2, 3]), field, name);
        return content;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SqlFact]
    public async Task PaidAnalysisRoutesPersistHistoryAndApplyFeatureAndRecordGuards()
    {
        await using var database = new SqlDatabase();
        await database.Db.Database.MigrateAsync();
        using var files = new TestFiles();
        await using var factory = new SubscriptionApiFactory(database.Db.Database.GetConnectionString(),
            new Dictionary<string, string?> { ["Storage:Root"] = files.Folder },
            services => services.AddScoped<IAiClient>(_ => new UnitAiClient()));
        using var client = await factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/analysis/image", File("bird.jpg"))).StatusCode);
        var session = await SubscriptionApiTests.Register(client, "paid-analysis@example.test");
        var userId = session.GetProperty("user").GetProperty("id").GetString()!;
        var db = database.Db;
        var order = new PaymentOrder { UserId = userId, PlanId = 2, OrderId = "paid-fixture", Amount = 10_000_000 };
        db.PaymentOrders.Add(order);
        db.UserSubscriptions.Add(new()
        {
            UserId = userId, PlanId = 2, PaymentOrderId = order.Id, StartDate = DateTimeOffset.UtcNow.AddDays(-1),
            EndDate = DateTimeOffset.UtcNow.AddDays(1), AnalysisLimit = 4,
            Features = """["image","audio","batch","history","gallery","statistics","advancedStatistics","compare"]""",
        });
        db.Birds.AddRange(new Bird { ScientificName = "Cardinalis cardinalis", UkrainianName = "Кардинал" },
                          new Bird { ScientificName = "Cyanocitta cristata", UkrainianName = "Сойка" });
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/analysis/sync/video", File("bird.jpg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/analysis/batch", new MultipartFormDataContent())).StatusCode);
        var first = await Json(await client.PostAsync("/api/analysis/sync/image", File("bird.jpg")), HttpStatusCode.Created);
        var second = await Json(await client.PostAsync("/api/analysis/sync/audio", File("song.wav")), HttpStatusCode.Created);
        var firstId = first.GetProperty("id").GetInt32();
        var secondId = second.GetProperty("id").GetInt32();
        Assert.Equal(3, first.GetProperty("predictions").GetArrayLength());
        Assert.Equal("Кардинал", first.GetProperty("bird").GetProperty("ukrainianName").GetString());
        var audio = await Json(await client.PostAsync("/api/analysis/audio", File("async.wav")), HttpStatusCode.Accepted);
        var status = await client.GetFromJsonAsync<JsonElement>(audio.GetProperty("statusUrl").GetString());
        Assert.Equal("Pending", status.GetProperty("status").GetString());
        var audioHistoryId = audio.GetProperty("historyId").GetInt32();
        await db.Analyses.Where(a => a.Id == audioHistoryId)
            .ExecuteUpdateAsync(update => update.SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-20)));
        status = await client.GetFromJsonAsync<JsonElement>(audio.GetProperty("statusUrl").GetString());
        Assert.True(status.GetProperty("stalled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/analyses/{audioHistoryId}")).StatusCode);
        using var batch = File("first.jpg", "files");
        batch.Add(new ByteArrayContent([5, 6]), "files", "second.jpg");
        var accepted = await Json(await client.PostAsync("/api/analysis/batch", batch), HttpStatusCode.Accepted);
        Assert.Equal(JsonValueKind.Object, accepted[0].GetProperty("task").ValueKind);
        Assert.Equal(JsonValueKind.Null, accepted[1].GetProperty("task").ValueKind);
        Assert.Contains("Ліміт", accepted[1].GetProperty("error").GetString());
        Assert.Equal((HttpStatusCode)429, (await client.PostAsync("/api/analysis/image", File("limit.jpg"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/analyses/compare?firstId={firstId}&secondId={audio.GetProperty("historyId").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/analyses/compare?firstId={firstId}&secondId={firstId}")).StatusCode);
        var comparison = await client.GetFromJsonAsync<JsonElement>($"/api/analyses/compare?firstId={firstId}&secondId={secondId}");
        Assert.Contains("той самий вид", comparison.GetProperty("conclusion").GetString());
        var birds = await client.GetFromJsonAsync<JsonElement>("/api/birds/search?search=Cardinalis");
        var bird = Assert.Single(birds.EnumerateArray());
        var birdId = bird.GetProperty("id").GetInt32();
        var jayId = await db.Birds.Where(b => b.CommonName == "" && b.ScientificName == "Cyanocitta cristata").Select(b => b.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/birds/compare?firstId={birdId}&secondId={jayId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/birds/compare?firstId={birdId}&secondId={birdId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/birds/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/birds?search=" + new string('x', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analyses?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analyses?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analyses?analysisType=video")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analyses?sort=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analyses?search=" + new string('x', 201))).StatusCode);
        foreach (var sort in new[] { "date_asc", "species", "confidence_desc" })
        {
            var sorted = await client.GetFromJsonAsync<JsonElement>($"/api/analyses?sort={sort}&search=song");
            Assert.Equal(secondId, Assert.Single(sorted.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        }

        var saved = await db.Analyses.SingleAsync(a => a.Id == firstId);
        saved.Evaluation = new Evaluation { ExpectedLabel = "Cardinal", Top1Correct = true, Top3Correct = true, Experiment = "fixture" };
        await db.SaveChangesAsync();
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/statistics/summary");
        Assert.Equal(2, summary.GetProperty("totalAnalyses").GetInt32());
        Assert.Equal(.8, summary.GetProperty("averageConfidence").GetDouble(), 4);
        var species = await client.GetFromJsonAsync<JsonElement>("/api/statistics/species");
        Assert.Equal(2, species[0].GetProperty("count").GetInt32());
        var bins = await client.GetFromJsonAsync<JsonElement>("/api/statistics/confidence");
        Assert.Equal(new[] { 0, 0, 0, 2 }, bins.EnumerateArray().Select(b => b.GetProperty("count").GetInt32()));
        var quality = await client.GetFromJsonAsync<JsonElement>("/api/statistics/evaluations");
        Assert.Equal(1, quality[0].GetProperty("top1Correct").GetInt32());
        Assert.Contains("bird.jpg", await client.GetStringAsync("/api/export/csv"));
        var gallery = await client.GetFromJsonAsync<JsonElement>("/api/analyses/gallery");
        Assert.All(gallery.GetProperty("items").EnumerateArray(), item => Assert.Equal("image", item.GetProperty("analysisType").GetString()));
        var file = await client.GetAsync($"/api/analyses/{firstId}/file");
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(file.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(new byte[] { 1, 2, 3 }, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/analyses/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/analyses/{firstId}")).StatusCode);
        Assert.False(System.IO.File.Exists(files.Store.Resolve(saved.StoredFilePath)));
        Assert.False(await db.Evaluations.AnyAsync(e => e.AnalysisId == firstId));
    }

    [Fact]
    public async Task AuthorizedArchiveReaderGetsLegacyFilesAndExpiredBearerCannotReadThem()
    {
        using var files = new TestFiles();
        await using var factory = new SubscriptionApiFactory(configuration: new Dictionary<string, string?> { ["Storage:Root"] = files.Folder });
        using var client = await factory.Client();
        var session = await SubscriptionApiTests.Register(client, "archive@example.test", bearer: true);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var user = session.GetProperty("user").GetProperty("id").GetString();
        factory.Services.GetRequiredService<IConfiguration>()["Auth:LegacyArchiveReaders:0"] = user;
        var stored = await files.Store.SaveAsync(new MemoryStream([1, 2, 3]), "legacy.jpg", "image", 3, default);
        int id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BirdDbContext>();
            var legacy = new Analysis { StoredFilePath = stored.Key, OriginalFileName = stored.OriginalName, PredictedSpecies = "Legacy" };
            db.Analyses.Add(legacy);
            db.Analyses.Add(new Analysis { UserId = user, StoredFilePath = "private.jpg" });
            await db.SaveChangesAsync();
            id = legacy.Id;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/legacy/archive?page=0")).StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/legacy/archive");
        var record = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("Legacy", record.GetProperty("predictedSpecies").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, await client.GetByteArrayAsync(record.GetProperty("fileUrl").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/legacy/archive/99999/file")).StatusCode);
        System.IO.File.Delete(files.Store.Resolve(stored.Key));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/legacy/archive/{id}/file")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BirdDbContext>();
            var token = await db.AuthSessions.SingleAsync();
            token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/legacy/archive")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bad-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [SqlFact]
    public async Task SignedWalletAndPaymentEndpointsKeepOrdersPrivateAndDoNotActivatePendingPayment()
    {
        await using var database = new SqlDatabase();
        await database.Db.Database.MigrateAsync();
        var config = SubscriptionTests.Options();
        var chain = new FakeBlockchain { Evidence = new("Pending") };
        await using var factory = new SubscriptionApiFactory(database.Db.Database.GetConnectionString(), new Dictionary<string, string?>
        {
            ["Blockchain:NetworkInstanceId"] = SubscriptionTests.NetworkId.ToString(), ["Blockchain:Enabled"] = "true", ["Blockchain:ChainId"] = "31337", ["Blockchain:AllowLocalAnvil"] = "true",
            ["Blockchain:TokenAddress"] = config.TokenAddress, ["Blockchain:PaymentContractAddress"] = config.PaymentContractAddress,
        }, services => services.AddSingleton<IBlockchainVerifier>(chain));
        using var client = await factory.Client();
        using var stranger = await factory.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { email = "broken", password = "weak" })).StatusCode);
        await SubscriptionApiTests.Register(client, "payment@example.test");
        await SubscriptionApiTests.Register(stranger, "stranger@example.test");
        var network = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/network");
        Assert.Equal(31337, network.GetProperty("chainId").GetInt64());
        var key = EthECKey.GenerateKey();
        var walletAddress = key.GetPublicAddress();
        var nonce = await Json(await client.PostAsJsonAsync("/api/wallets/nonce", new { walletAddress }), HttpStatusCode.OK);
        var message = nonce.GetProperty("message").GetString()!;
        var proof = new { nonce = nonce.GetProperty("nonce").GetString(), message, signature = new EthereumMessageSigner().EncodeUTF8AndSign(message, key) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/wallets/verify", proof with { signature = "0x00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsJsonAsync("/api/wallets/verify", proof)).StatusCode);
        var verified = await Json(await client.PostAsJsonAsync("/api/wallets/verify", proof), HttpStatusCode.OK);
        Assert.True(verified.GetProperty("isVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/wallets/verify", proof)).StatusCode);
        var wallets = await client.GetFromJsonAsync<JsonElement>("/api/wallets/me");
        Assert.Equal(walletAddress.ToLowerInvariant(), Assert.Single(wallets.EnumerateArray()).GetProperty("walletAddress").GetString());

        var order = await Json(await client.PostAsJsonAsync("/api/subscriptions/orders", new { planId = 1, walletAddress, amount = 1 }), HttpStatusCode.Created);
        var orderId = order.GetProperty("id").GetGuid();
        Assert.Equal("5000000", order.GetProperty("amount").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/subscriptions/orders/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/subscriptions/orders/{orderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions/payments/verify", new { orderId, transactionHash = "0x00" })).StatusCode);
        var transaction = new { orderId, transactionHash = "0x" + new string('a', 64) };
        var pending = await Json(await client.PostAsJsonAsync("/api/subscriptions/payments/verify", transaction), HttpStatusCode.OK);
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Equal("FREE", (await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/usage")).GetProperty("plan").GetString());
        chain.Evidence = new("Confirmed", null, 10, "0x" + new string('b', 64), 1);
        await Json(await client.PostAsJsonAsync("/api/subscriptions/payments/verify", transaction), HttpStatusCode.OK);
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/history")).EnumerateArray());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/payments")).EnumerateArray());
        Assert.Equal("PRO", (await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/me")).GetProperty("plan").GetString());
        Assert.Empty((await stranger.GetFromJsonAsync<JsonElement>("/api/subscriptions/payments")).EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicHealthReportsSeparateDependencyStateWhenWorkerUnavailable(bool aiAvailable)
    {
        using var files = new TestFiles();
        await using var factory = new SubscriptionApiFactory(
            configuration: new Dictionary<string, string?> { ["Storage:Root"] = files.Folder },
            configureServices: services => services.AddHttpClient("ai-health").ConfigurePrimaryHttpMessageHandler(
                () => aiAvailable ? new ResponseHandler(new HttpResponseMessage(HttpStatusCode.OK))
                    : new ResponseHandler(error: new HttpRequestException("test unavailable"))));
        using var client = await factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/live")).StatusCode);
        var health = await Json(await client.GetAsync("/api/health"), HttpStatusCode.ServiceUnavailable);
        Assert.Equal("degraded", health.GetProperty("status").GetString());
        Assert.Equal(aiAvailable, health.GetProperty("ai").GetBoolean());
        Assert.False(health.GetProperty("rabbitmq").GetBoolean());
        Assert.False(health.GetProperty("worker").GetBoolean());
    }
}
