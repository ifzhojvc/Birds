using System.Net;
using System.Text.Json;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Models;
using BirdLab.Api.Subscriptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Util;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed partial class BlockchainRpcTests
{
    private static string Hex(string digits) => "0x" + digits;
    private static string Word(string digits) => digits.PadLeft(64, '0');
    private static string Selector(string signature) => Hex(new Sha3Keccack().CalculateHash(signature)[..8]);
    private static string Hash(char ch) => Hex(new string(ch, 64));

    private sealed record RpcFailure(int Code, string Message);

    private sealed class Rpc : IAsyncDisposable
    {
        private readonly WebApplication app;
        public string Url => app.Urls.Single();
        private Rpc(WebApplication app) => this.app = app;

        public static async Task<Rpc> StartAsync(Func<string, JsonElement, object?> respond)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapPost("/", async (HttpRequest request) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body);
                var json = body.RootElement;
                var method = json.GetProperty("method").GetString()!;
                var parameters = json.TryGetProperty("params", out var value) ? value : default;
                var result = respond(method, parameters);
                if (result is RpcFailure failure) return Results.Json(new { jsonrpc = "2.0", id = json.GetProperty("id").Clone(), error = new { code = failure.Code, message = failure.Message } });
                return Results.Json(new { jsonrpc = "2.0", id = json.GetProperty("id").Clone(), result });
            });
            await app.StartAsync();
            return new(app);
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    [Theory]
    [InlineData("valid", "")]
    [InlineData("chain", "іншої мережі")]
    [InlineData("missing-contract", "відсутній")]
    [InlineData("token", "Ціна, токен")]
    [InlineData("price", "Ціна, токен")]
    [InlineData("decimals", "Ціна, токен")]
    [InlineData("paused", "Ціна, токен")]
    public async Task VerifiesConfiguredPaymentTermsThroughJsonRpc(string scenario, string error)
    {
        using var local = new LocalFiles();
        var config = local.Config;
        var selectors = new Dictionary<string, string>
        {
            [Selector("paymentToken()")] = Hex(Word(scenario == "token" ? new string('f', 40) : config.TokenAddress[2..])),
            [Selector("prices(uint256)") + Word("2")] = Hex(Word(scenario == "price" ? "1" : "989680")),
            [Selector("paused()")] = Hex(Word(scenario == "paused" ? "1" : "0")),
            [Selector("decimals()")] = Hex(Word(scenario == "decimals" ? "18" : "6")),
        };
        await using var rpc = await Rpc.StartAsync((method, parameters) =>
        {
            if (method is "eth_getBlockByNumber" or "eth_getTransactionByHash" or "eth_getTransactionReceipt" || method == "eth_call" && parameters[0].GetProperty("data").GetString() == Selector("owner()")) return local.Respond("valid", method, parameters);
            return method switch
        {
            "eth_chainId" => scenario == "chain" ? "0x1" : "0x7a69",
            "eth_getCode" => scenario == "missing-contract" ? "0x" : "0x1234",
            "eth_call" => selectors[parameters[0].GetProperty("data").GetString()!.ToLowerInvariant()],
            _ => throw new InvalidOperationException(method)
        };
        });
        config.RpcUrl = rpc.Url;
        var verifier = new BlockchainVerifier(Options.Create(config));
        if (error.Length == 0) await verifier.CheckConfigurationAsync(2, 10_000_000, default);
        else
        {
            var failure = await Assert.ThrowsAsync<ApiException>(() => verifier.CheckConfigurationAsync(2, 10_000_000, default));
            Assert.Equal(503, failure.Status);
            Assert.Contains(error, failure.Message);
        }
    }

    [Theory]
    [InlineData("confirmed", "Confirmed")]
    [InlineData("confirmations", "Pending")]
    [InlineData("configured-chain", "Rejected")]
    [InlineData("configured-token", "Rejected")]
    [InlineData("configured-contract", "Rejected")]
    [InlineData("rpc-chain", "RpcUnavailable")]
    [InlineData("unknown-tx", "Pending")]
    [InlineData("hash", "Rejected")]
    [InlineData("recipient", "Rejected")]
    [InlineData("sender", "Rejected")]
    [InlineData("value", "Rejected")]
    [InlineData("calldata", "Rejected")]
    [InlineData("unmined", "Pending")]
    [InlineData("reverted", "Rejected")]
    [InlineData("receipt", "Rejected")]
    [InlineData("no-block", "Reorged")]
    [InlineData("reorg", "Reorged")]
    [InlineData("expired", "Rejected")]
    [InlineData("early", "Rejected")]
    [InlineData("wrong-emitter", "Rejected")]
    [InlineData("duplicate-log", "Rejected")]
    [InlineData("wrong-order", "Rejected")]
    [InlineData("wrong-payer", "Rejected")]
    [InlineData("wrong-plan", "Rejected")]
    [InlineData("wrong-amount", "Rejected")]
    [InlineData("wrong-token", "Rejected")]
    public async Task VerifiesReceiptAgainstTheExactOrderAndCanonicalChain(string scenario, string expected)
    {
        using var local = new LocalFiles();
        var config = local.Config;
        var timestamp = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var wallet = "0x3333333333333333333333333333333333333333";
        var other = "0x4444444444444444444444444444444444444444";
        var order = new PaymentOrder
        {
            OrderId = wallet + new string('a', 24), WalletAddress = wallet, PlanId = 1, Amount = 5_000_000,
            ChainId = scenario == "configured-chain" ? 1 : config.ChainId,
            TokenContractAddress = scenario == "configured-token" ? other : config.TokenAddress,
            PaymentContractAddress = scenario == "configured-contract" ? other : config.PaymentContractAddress,
            CreatedAt = timestamp.AddMinutes(-1), ExpiresAt = timestamp.AddMinutes(1),
        };
        var hash = Hash('a');
        var blockHash = Hash('b');
        var tx = new
        {
            hash = scenario == "hash" ? Hash('c') : hash, blockHash,
            from = scenario == "sender" ? other : wallet,
            to = scenario == "recipient" ? other : config.PaymentContractAddress,
            value = scenario == "value" ? "0x1" : "0x0",
            input = scenario == "calldata" ? "0xdeadbeef" : Selector("purchaseSubscription(bytes32,uint256)") + order.OrderId[2..] + Word("1"),
        };
        var log = new
        {
            address = scenario == "wrong-emitter" ? other : config.PaymentContractAddress,
            logIndex = "0x1",
            topics = new[]
            {
                Hex(new Sha3Keccack().CalculateHash("SubscriptionPurchased(bytes32,address,uint256,uint256,address)")),
                scenario == "wrong-order" ? Hash('d') : order.OrderId,
                Hex(Word((scenario == "wrong-payer" ? other : wallet)[2..])),
                Hex(Word(scenario == "wrong-plan" ? "2" : "1")),
            },
            data = Hex(Word(scenario == "wrong-amount" ? "1" : "4c4b40") +
                       Word((scenario == "wrong-token" ? other : config.TokenAddress)[2..]))
        };
        var receipt = new
        {
            transactionHash = hash, blockHash, blockNumber = "0x10",
            status = scenario == "reverted" ? "0x0" : "0x1",
            from = scenario == "receipt" ? other : wallet, to = config.PaymentContractAddress,
            logs = scenario == "duplicate-log" ? new[] { log, log } : new[] { log },
        };
        var mined = scenario == "early" ? timestamp.AddMinutes(-3) : scenario == "expired" ? timestamp.AddMinutes(3) : timestamp;
        var methods = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var rpc = await Rpc.StartAsync((method, parameters) =>
        {
            if (method is "eth_getCode" or "eth_call" || method == "eth_getTransactionReceipt" && parameters[0].GetString() is var deploymentHash && (deploymentHash == Hash('7') || deploymentHash == Hash('8') || deploymentHash == Hash('9')) || method == "eth_getTransactionByHash" && parameters[0].GetString() == Hash('9') || method == "eth_getBlockByNumber" && parameters[0].GetString() is "0x1" or "0x2" or "0x3") return local.Respond("valid", method, parameters);
            methods.Enqueue(method);
            return method switch
            {
                "eth_chainId" => scenario == "rpc-chain" ? "0x1" : "0x7a69",
                "eth_getTransactionByHash" => scenario == "unknown-tx" ? null : tx,
                "eth_getTransactionReceipt" => scenario == "unmined" ? null : receipt,
                "eth_getBlockByNumber" => scenario == "no-block" ? null : new
                {
                    hash = scenario == "reorg" ? Hash('e') : blockHash,
                    timestamp = Hex(mined.ToUnixTimeSeconds().ToString("x")),
                    transactions = new[] { hash },
                },
                "eth_blockNumber" => scenario == "confirmations" ? "0x10" : "0x11",
                _ => throw new InvalidOperationException(method),
            };
        });
        config.RpcUrl = rpc.Url;
        var result = await new BlockchainVerifier(Options.Create(config)).VerifyAsync(order, hash, default);
        Assert.Equal(expected, result.State);
        if (scenario is "confirmed" or "confirmations")
        {
            Assert.Equal(16, result.BlockNumber);
            Assert.Equal(blockHash, result.BlockHash);
            Assert.Equal(1, result.LogIndex);
        }
        else
        {
            Assert.NotEmpty(result.Error!);
            Assert.DoesNotContain("eth_blockNumber", methods);
        }
        if (scenario.StartsWith("configured-")) Assert.Empty(methods);
    }
}
