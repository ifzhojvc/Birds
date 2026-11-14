using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Messaging;
using BirdLab.Api.Services;
using BirdLab.Api.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData(".PNG", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".WAV", "audio/wav")]
    [InlineData(".mp3", "audio/mpeg")]
    [InlineData(".flac", "audio/flac")]
    [InlineData(".m4a", "audio/mp4")]
    [InlineData(".aac", "audio/aac")]
    [InlineData(".ogg", "audio/ogg")]
    [InlineData(".unknown", "application/octet-stream")]
    public void ContentTypeMatchesSupportedClientUploads(string extension, string media)
    {
        Assert.Equal(media, FileStore.ContentType("sample" + extension));
        if (extension != ".unknown") FileStore.Validate("sample" + extension, 1, media.Split('/')[0]);
    }

    [Theory]
    [InlineData("wrong.jpg", "audio", 42, 422)]
    [InlineData("song.wav", "audio", 52428801, 413)]
    [InlineData("song.wav", "audio", 0, 413)]
    public void AudioValidationRejectsInvalidMediaBeforeSaving(string name, string kind, long size, int expected)
        => Assert.Equal(expected, Assert.Throws<ApiException>(() => FileStore.Validate(name, size, kind)).Status);

    [Fact]
    public void RejectsLongFileNamesAndDirectoryTargets()
    {
        using var files = new TestFiles();
        Assert.Equal(422, Assert.Throws<ApiException>(() => FileStore.Validate(new string('x', 256) + ".jpg", 1, "image")).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => files.Store.Resolve("")).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => files.Store.Resolve(".")).Status);
    }

    [Theory]
    [InlineData("api", 409, "explicit public message")]
    [InlineData("http", 413, "explicit public message")]
    [InlineData("db", 503, "База даних недоступна")]
    [InlineData("unexpected", 500, "Внутрішня помилка")]
    public async Task ProblemDetailsExposePublicErrorsAndRedactPrivateFailures(string kind, int status, string detail)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var output = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "test-request-id" };
        http.Response.Body = output;
        Exception error = kind switch
        {
            "api" => new ApiException(status, detail),
            "http" => new BadHttpRequestException(detail, status),
            "db" => new DbUpdateException("internal database detail"),
            _ => new InvalidOperationException("internal implementation detail"),
        };
        Assert.True(await new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance).TryHandleAsync(http, error, default));
        Assert.Equal(status, http.Response.StatusCode);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.Contains(detail, json.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("internal", json.RootElement.GetRawText());
        Assert.Equal("test-request-id", json.RootElement.GetProperty("traceId").GetString());
    }

    [Theory]
    [InlineData("empty-rpc")]
    [InlineData("non-http")]
    [InlineData("insecure-sepolia")]
    [InlineData("negative-decimals")]
    [InlineData("excessive-decimals")]
    [InlineData("zero-confirmations")]
    [InlineData("excessive-confirmations")]
    [InlineData("zero-timeout")]
    [InlineData("excessive-timeout")]
    [InlineData("different-siwe-origin")]
    [InlineData("invalid-siwe-scheme")]
    public void MalformedBlockchainConfigNeverEnablesPayments(string scenario)
    {
        var options = SubscriptionTests.Options();
        switch (scenario)
        {
            case "empty-rpc": options.RpcUrl = ""; break;
            case "non-http": options.RpcUrl = "file:///some/path"; break;
            case "insecure-sepolia": options.ChainId = 84532; options.RpcUrl = "http://node.test"; break;
            case "negative-decimals": options.TokenDecimals = -1; break;
            case "excessive-decimals": options.TokenDecimals = 19; break;
            case "zero-confirmations": options.Confirmations = 0; break;
            case "excessive-confirmations": options.Confirmations = 1001; break;
            case "zero-timeout": options.OrderMinutes = 0; break;
            case "excessive-timeout": options.OrderMinutes = 121; break;
            case "different-siwe-origin": options.SiweUri = "https://different.test"; break;
            case "invalid-siwe-scheme": options.SiweUri = "ftp://localhost:5173"; break;
        }
        Assert.Equal(503, Assert.Throws<ApiException>(options.RequirePayments).Status);
    }

    private static JsonObject CompletedEvent()
    {
        var result = new AnalysisEvent(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "completed",
            DateTimeOffset.UtcNow, 0, [new("first", "Cardinalis cardinalis", .8, 1), new("second", null, .15, 2), new("third", null, .05, 3)],
            "first", .8, "image-classifier", "revision", .1, JsonSerializer.SerializeToElement(new { mock_used = false }));
        return JsonSerializer.SerializeToNode(result, EventJson.Options)!.AsObject();
    }

    [Theory]
    [InlineData("eventId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("analysisId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("correlationId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("causationId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("occurredAt", "\"0001-01-01T00:00:00Z\"")]
    [InlineData("occurredAt", "\"2026-10-30T12:00:00+02:00\"")]
    [InlineData("retryCount", "-1")]
    [InlineData("retryCount", "4")]
    [InlineData("status", "\"Processing\"")]
    [InlineData("predictions", "null")]
    [InlineData("predictions", "[]")]
    [InlineData("predictions.0", "null")]
    [InlineData("predictions.0.rank", "3")]
    [InlineData("predictions.1.species", "\"first\"")]
    [InlineData("predictions.0.species", "\" \"")]
    [InlineData("predictions.0.confidence", "-0.1")]
    [InlineData("predictions.0.confidence", "1.1")]
    [InlineData("predictions.1.confidence", "0.95")]
    [InlineData("predictions.2.confidence", "0.25")]
    [InlineData("predictedSpecies", "\"different\"")]
    [InlineData("confidence", "0.6")]
    [InlineData("modelName", "\" \"")]
    [InlineData("modelRevision", "\"\"")]
    [InlineData("elapsedSeconds", "null")]
    [InlineData("elapsedSeconds", "-0.1")]
    [InlineData("metadata", "[]")]
    [InlineData("metadata", "{}")]
    [InlineData("metadata.mock_used", "true")]
    [InlineData("metadata.mock_used", "\"false\"")]
    public void InvalidWorkerResultsCannotPassTheEnvelopeValidation(string field, string jsonValue)
    {
        var payload = CompletedEvent();
        var segments = field.Split('.');
        JsonNode node = payload;
        foreach (var segment in segments[..^1])
            node = node is JsonArray array ? array[int.Parse(segment)]! : node[segment]!;
        if (node is JsonArray items) items[int.Parse(segments[^1])] = JsonNode.Parse(jsonValue);
        else node[segments[^1]] = JsonNode.Parse(jsonValue);
        Assert.Throws<InvalidEventException>(() => EventJson.Read(Encoding.UTF8.GetBytes(payload.ToJsonString())));
    }

    [Theory]
    [InlineData("predictions", 0, "species", 257)]
    [InlineData("predictions", 0, "scientificName", 257)]
    [InlineData("modelName", -1, "", 513)]
    [InlineData("modelRevision", -1, "", 257)]
    public void WorkerEventRejectsDataThatCannotFitSqlColumns(string field, int index, string key, int length)
    {
        var payload = CompletedEvent();
        if (index < 0) payload[field] = new string('x', length);
        else payload[field]![index]![key] = new string('x', length);
        Assert.Throws<InvalidEventException>(() => EventJson.Read(Encoding.UTF8.GetBytes(payload.ToJsonString())));
    }

    [Fact]
    public void InvalidAndOversizedWorkerEnvelopesAreRejectedBeforeDatabaseAccess()
    {
        Assert.Throws<InvalidEventException>(() => EventJson.Read(new byte[65537]));
        Assert.Throws<InvalidEventException>(() => EventJson.Read("null"u8));
        var failed = CompletedEvent();
        failed["status"] = "failed";
        failed["errorMessage"] = new string('x', 2001);
        Assert.Throws<InvalidEventException>(() => EventJson.Read(Encoding.UTF8.GetBytes(failed.ToJsonString())));
    }

    private static AiClient Ai(HttpResponseMessage? response = null, Exception? error = null)
        => new(new HttpClient(new ResponseHandler(response, error)) { BaseAddress = new Uri("http://ai.test") });

    [Theory]
    [InlineData("null-prediction")]
    [InlineData("unordered-confidence")]
    [InlineData("third-outranks-second")]
    [InlineData("negative-elapsed")]
    [InlineData("same-label")]
    [InlineData("valid")]
    public async Task SyncAiRejectsMalformedTopThreeAsUpstreamFailure(string scenario)
    {
        var payload = JsonNode.Parse("""
            {"analysis_type":"audio","model_id":"fixture","model_revision":"revision","elapsed_seconds":0.25,
             "predictions":[{"species_name":"first","scientific_name":null,"confidence":0.8,"rank":1},
                            {"species_name":"second","scientific_name":null,"confidence":0.15,"rank":2},
                            {"species_name":"third","scientific_name":null,"confidence":0.05,"rank":3}],
             "metadata":{"mock_used":false}}
            """)!.AsObject();
        var predictions = payload["predictions"]!.AsArray();
        switch (scenario)
        {
            case "null-prediction": predictions[1] = null; break;
            case "unordered-confidence": predictions[0]!["confidence"] = .1; break;
            case "third-outranks-second": predictions[2]!["confidence"] = .3; break;
            case "negative-elapsed": payload["elapsed_seconds"] = -1; break;
            case "same-label": predictions[2]!["species_name"] = "first"; break;
        }
        var ai = Ai(new(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") });
        if (scenario == "valid")
        {
            var result = await ai.PredictAsync(new MemoryStream([1]), "bird.wav", "audio", default);
            Assert.Equal("audio", result.AnalysisType);
            Assert.Equal(new[] { 1, 2, 3 }, result.Predictions.Select(p => p.Rank));
        }
        else
            Assert.Equal(502, (await Assert.ThrowsAsync<ApiException>(() => ai.PredictAsync(new MemoryStream([1]), "bird.wav", "audio", default))).Status);
    }

    [Theory]
    [InlineData(413, "broken", 413)]
    [InlineData(503, "{\"message\":null}", 502)]
    [InlineData(200, "bad JSON", 502)]
    public async Task SyncAiPreservesSizeErrorAndHandlesNonJsonUpstreamBodies(int status, string payload, int expected)
    {
        var ai = Ai(new((HttpStatusCode)status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        Assert.Equal(expected, (await Assert.ThrowsAsync<ApiException>(() => ai.PredictAsync(new MemoryStream([1]), "bird.jpg", "image", default))).Status);
    }

    [Fact]
    public async Task UnreachableInferenceServiceIsBadGateway()
    {
        var ai = Ai(error: new HttpRequestException("test connection refused"));
        Assert.Equal(502, (await Assert.ThrowsAsync<ApiException>(() => ai.PredictAsync(new MemoryStream([1]), "bird.jpg", "image", default))).Status);
    }
}
