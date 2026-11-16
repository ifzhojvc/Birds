using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Messaging;
using BirdLab.Api.Models;
using BirdLab.Api.Services;
using BirdLab.Api.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class MediaRejectionTests
{
    private const string ImageAbsent = "На зображенні не вдалося виявити птаха. Спробуйте завантажити іншу фотографію.";
    private const string ImageUncertain = "Не вдалося впевнено визначити, чи є на фото птах. Спробуйте чіткіше зображення.";
    private const string AudioAbsent = "На аудіозаписі не вдалося виявити звуків птахів. Завантажте інший запис.";
    private const string AudioSpecies = "Звуки птаха виявлено, але визначити вид із достатньою впевненістю не вдалося.";
    private sealed class RejectingAi(string message) : IAiClient
    {
        public Task<BirdLab.Api.DTOs.AiResult> PredictAsync(Stream stream, string name, string kind, CancellationToken token) => Task.FromException<BirdLab.Api.DTOs.AiResult>(new ApiException(422, message));
    }

    private static async Task Pay(BirdDbContext db)
    {
        var (payments, order, _) = await SubscriptionTests.Order(db);
        var payment = await payments.SubmitAsync(TestAccount.UserId, order.Id, "0x" + new string('c', 64), default);
        await payments.ProcessAsync(payment.Id, default);
    }

    [Theory]
    [InlineData("image", false, ImageAbsent)]
    [InlineData("image", false, ImageUncertain)]
    [InlineData("image", true, ImageAbsent)]
    [InlineData("audio", true, AudioAbsent)]
    [InlineData("audio", true, AudioSpecies)]
    public async Task ContentFailureIsTerminalAndReleasesFreeAndPaidQuotaExactlyOnce(string kind, bool paid, string message)
    {
        await using var db = await SubscriptionTests.Database();
        if (paid) await Pay(db);
        using var files = new TestFiles();
        var service = await TestAccount.Analyses(db, new RejectingAi(message), files.Store);
        var subscriptions = new SubscriptionService(db);
        var before = await subscriptions.GetAsync(TestAccount.UserId, default);
        var task = await service.EnqueueAsync(new FormFile(new MemoryStream([1]), 0, 1, "file", kind == "image" ? "nonbird.jpg" : "speech.wav"), kind, default);
        Assert.Equal(before.Remaining - 1, (await subscriptions.GetAsync(TestAccount.UserId, default)).Remaining);
        var analysis = await db.Analyses.SingleAsync();
        var request = await db.OutboxMessages.SingleAsync();
        var failed = new AnalysisEvent(1, Guid.NewGuid(), task.AnalysisId, analysis.CorrelationId, request.Id, "failed", DateTimeOffset.UtcNow, 0, ErrorMessage: message);
        var body = JsonSerializer.SerializeToUtf8Bytes(failed, EventJson.Options);
        var processor = new ResultProcessor(db);
        await processor.ApplyAsync(body, default);
        await processor.ApplyAsync(body, default);
        await processor.ApplyAsync(JsonSerializer.SerializeToUtf8Bytes(failed with { EventId = Guid.NewGuid(), Status = "processing", ErrorMessage = null }, EventJson.Options), default);
        var status = await service.StatusAsync(task.AnalysisId, default);
        Assert.Equal("Failed", status.Status);
        Assert.Equal(message, status.ErrorMessage);
        Assert.Null(status.Result);
        Assert.Empty(db.Predictions);
        Assert.Empty((await service.HistoryAsync(1, 20, null, null, "date_desc", default)).Items);
        Assert.Equal("Released", (await db.UsageRecords.SingleAsync()).Status);
        Assert.Equal(before.Remaining, (await subscriptions.GetAsync(TestAccount.UserId, default)).Remaining);
        Assert.Equal<int?>(paid ? 0 : null, (await db.UserSubscriptions.SingleOrDefaultAsync())?.AnalysesUsed);
    }

    [Theory]
    [InlineData("image", false, ImageAbsent)]
    [InlineData("audio", true, AudioAbsent)]
    public async Task SynchronousContentFailureReturns422WithoutSpendingQuota(string kind, bool paid, string message)
    {
        await using var db = await SubscriptionTests.Database();
        if (paid) await Pay(db);
        using var files = new TestFiles();
        var service = await TestAccount.Analyses(db, new RejectingAi(message), files.Store);
        var subscriptions = new SubscriptionService(db);
        var before = (await subscriptions.GetAsync(TestAccount.UserId, default)).Remaining;
        var error = await Assert.ThrowsAsync<ApiException>(() => service.AnalyzeAsync(new FormFile(new MemoryStream([1]), 0, 1, "file", kind == "image" ? "cat.jpg" : "speech.wav"), kind, default));
        Assert.Equal(422, error.Status);
        Assert.Equal(message, error.Message);
        Assert.Equal(before, (await subscriptions.GetAsync(TestAccount.UserId, default)).Remaining);
        Assert.Equal("Released", (await db.UsageRecords.SingleAsync()).Status);
        Assert.Empty(db.Analyses);
        Assert.Empty(db.Predictions);
        Assert.Empty(Directory.GetFiles(files.Folder));
    }

    [Fact]
    public async Task HttpSyncRejectsNonBirdWithTheOriginalUkrainianMessage()
    {
        using var files = new TestFiles();
        await using var factory = new SubscriptionApiFactory(configuration: new Dictionary<string, string?> { ["Storage:Root"] = files.Folder },
            configureServices: services => services.AddScoped<IAiClient>(_ => new RejectingAi(ImageAbsent)));
        using var client = await factory.Client();
        await SubscriptionApiTests.Register(client, "nonbird@example.test");
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent([1, 2]), "file", "cat.jpg");
        var response = await client.PostAsync("/api/analysis/sync/image", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ImageAbsent, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(5, (await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/me")).GetProperty("remaining").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/analyses")).GetProperty("total").GetInt32());
    }

    [SqlFact]
    public async Task SqlContentFailureAndGuardPreservePaidSubscriptionAndReleaseQuota()
    {
        await using var database = new SqlDatabase();
        var db = database.Db;
        await db.Database.MigrateAsync();
        using var files = new TestFiles();
        await TestAccount.Analyses(db, new UnitAiClient(), files.Store);
        await Pay(db);
        var service = await TestAccount.Analyses(db, new RejectingAi(AudioAbsent), files.Store);
        var task = await service.EnqueueAsync(new FormFile(new MemoryStream([1]), 0, 1, "file", "speech.wav"), "audio", default);
        var analysis = await db.Analyses.SingleAsync();
        var request = await db.OutboxMessages.SingleAsync();
        var body = JsonSerializer.SerializeToUtf8Bytes(new AnalysisEvent(1, Guid.NewGuid(), task.AnalysisId, analysis.CorrelationId, request.Id, "failed", DateTimeOffset.UtcNow, 0, ErrorMessage: AudioAbsent), EventJson.Options);
        await new ResultProcessor(db).ApplyAsync(body, default);
        db.ChangeTracker.Clear();
        await new ResultProcessor(db).ApplyAsync(body, default);
        Assert.Equal(100, (await new SubscriptionService(db).GetAsync(TestAccount.UserId, default)).Remaining);
        Assert.Equal("Released", (await db.UsageRecords.SingleAsync()).Status);
        Assert.Equal("Active", (await db.UserSubscriptions.SingleAsync()).Status);
        await LocalBlockchainGuard.WriteAsync(db, Path.Combine(files.Folder, "deployment.json"), default);
        using var guard = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.Folder, "sql-guard.json")));
        Assert.True(guard.RootElement.GetProperty("hasLocalPaymentHistory").GetBoolean());
    }
}
