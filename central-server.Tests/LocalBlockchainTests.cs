using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirdLab.Api.Data;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Models;
using BirdLab.Api.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed partial class BlockchainRpcTests
{
    private sealed class LocalFiles : IDisposable
    {
        private readonly TestFiles files = new();
        public BlockchainOptions Config { get; } = SubscriptionTests.Options();
        public JsonObject Document { get; }
        public LocalFiles()
        {
            Config.LocalDeploymentPath = Path.Combine(files.Folder, "deployment.json");
            Document = JsonSerializer.SerializeToNode(new
            {
                version = 1, testOnly = true, chainId = 31337, tokenDecimals = 6,
                tokenAddress = Config.TokenAddress, paymentContractAddress = Config.PaymentContractAddress,
                ownerAddress = "0x3333333333333333333333333333333333333333",
                tokenDeploymentTransactionHash = Hash('7'), deploymentTransactionHash = Hash('8'),
                deploymentBlockNumber = 2, deploymentBlockHash = Hash('d'),
                checkpointBlockNumber = 3, checkpointBlockHash = Hash('e'),
                networkIdentityTransactionHash = Hash('9'), networkIdentityData = Hash('6'),
                tokenRuntimeCodeHash = LocalDeployment.CodeHash("0x1234"), paymentRuntimeCodeHash = LocalDeployment.CodeHash("0x1234"),
                plans = new Dictionary<string, long> { ["1"] = 5_000_000, ["2"] = 10_000_000 }
            })!.AsObject();
            Save();
        }
        public void Save() => File.WriteAllText(Config.LocalDeploymentPath, Document.ToJsonString());
        public void Dispose() => files.Dispose();
        public object? Respond(string scenario, string method, JsonElement parameters)
        {
            if (method == "eth_chainId") return scenario == "chain" ? "0x1" : "0x7a69";
            if (method == "eth_getCode") return scenario == "missing-token" && parameters[0].GetString() == Config.TokenAddress ? "0x" : scenario == "code" ? "0x5678" : "0x1234";
            if (method == "eth_getBlockByNumber")
            {
                if (scenario == "missing-block") return null;
                var number = parameters[0].GetString();
                return new { hash = scenario == "block" ? Hash('0') : number == "0x2" ? Hash('d') : number == "0x3" ? Hash('e') : Hash('f') };
            }
            if (method == "eth_getTransactionReceipt")
            {
                var hash = parameters[0].GetString();
                if (hash == Hash('9')) return scenario == "missing-marker-receipt" ? null : new
                {
                    status = scenario == "marker-status" ? "0x0" : "0x1",
                    blockNumber = scenario == "marker-number" ? "0x2" : "0x3",
                    blockHash = scenario == "marker-block" ? Hash('0') : Hash('e'),
                };
                if (scenario == "missing-receipt" || scenario == "history-missing-receipt" && hash == Hash('c')) return null;
                return new
                {
                    status = scenario == "receipt-status" ? "0x0" : "0x1",
                    contractAddress = scenario == "deployment-address" ? Config.PaymentContractAddress : hash == Hash('7') ? Config.TokenAddress : Config.PaymentContractAddress,
                    to = scenario == "history-recipient" ? Config.TokenAddress : Config.PaymentContractAddress,
                    transactionHash = scenario == "history-hash" ? Hash('0') : hash,
                    blockNumber = hash == Hash('7') ? "0x1" : hash == Hash('8') ? "0x2" : "0x9",
                    blockHash = scenario is "history-block" or "deployment-receipt-block" ? Hash('0') : hash == Hash('8') ? Hash('d') : Hash('f'),
                };
            }
            if (method == "eth_call")
            {
                var data = parameters[0].GetProperty("data").GetString()!;
                if (data == Selector("owner()")) return parameters[0].GetProperty("to").GetString() == Config.TokenAddress
                    ? new RpcFailure(3, "execution reverted: function selector was not recognized")
                    : Hex(Word(new string(scenario == "owner" ? '4' : '3', 40)));
                if (data == Selector("paymentToken()")) return Hex(Word(Config.TokenAddress[2..]));
                if (data == Selector("decimals()")) return Hex(Word("6"));
                if (data == Selector("paused()")) return Hex(Word("0"));
                return Hex(Word(data.EndsWith(Word("1")) ? "4c4b40" : "989680"));
            }
            if (method == "eth_getTransactionByHash") return scenario != "missing-marker" && parameters[0].GetString() == Hash('9') ? new
            {
                hash = scenario == "marker-txhash" ? Hash('0') : Hash('9'), blockHash = scenario == "marker-txblock" ? Hash('0') : Hash('e'),
                from = scenario == "marker-from" ? Config.TokenAddress : Document["ownerAddress"]!.GetValue<string>(),
                to = scenario == "marker-to" ? Config.TokenAddress : Document["ownerAddress"]!.GetValue<string>(),
                value = scenario == "marker-value" ? "0x1" : "0x0", input = scenario == "marker-data" ? Hash('0') : Hash('6')
            } : null;
            throw new InvalidOperationException(method);
        }
        public CryptoPayment Paid() => new()
        {
            ChainId = 31337, TokenContractAddress = Config.TokenAddress, PaymentContractAddress = Config.PaymentContractAddress,
            TransactionHash = Hash('c'), BlockNumber = 9, BlockHash = Hash('f'), ConfirmedAt = DateTimeOffset.UtcNow, Status = "Confirmed"
        };
    }

    [Fact]
    public async Task MockUsdcOwnerRevertsWhileLocalPaymentReadinessRemainsValid()
    {
        using var local = new LocalFiles();
        var owners = new List<string>();
        await using var rpc = await Rpc.StartAsync((method, parameters) =>
        {
            if (method == "eth_call" && parameters[0].GetProperty("data").GetString() == Selector("owner()")) owners.Add(parameters[0].GetProperty("to").GetString()!);
            return local.Respond("valid", method, parameters);
        });
        local.Config.RpcUrl = rpc.Url;
        using var http = new HttpClient();
        var rejection = await http.PostAsJsonAsync(rpc.Url, new { jsonrpc = "2.0", id = 1, method = "eth_call", @params = new object[] { new { to = local.Config.TokenAddress, data = Selector("owner()") }, "latest" } });
        var payload = await rejection.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, payload.GetProperty("error").GetProperty("code").GetInt32());
        owners.Clear();
        await new BlockchainVerifier(Options.Create(local.Config)).CheckConfigurationAsync(1, 5_000_000, default);
        Assert.Equal(local.Config.PaymentContractAddress, Assert.Single(owners));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("chain")]
    [InlineData("missing-token")]
    [InlineData("code")]
    [InlineData("missing-block")]
    [InlineData("block")]
    [InlineData("missing-receipt")]
    [InlineData("receipt-status")]
    [InlineData("deployment-address")]
    [InlineData("owner")]
    [InlineData("manifest-plan")]
    [InlineData("manifest-plan-missing")]
    [InlineData("deployment-receipt-block")]
    [InlineData("missing-marker")]
    [InlineData("missing-marker-receipt")]
    [InlineData("marker-status")]
    [InlineData("marker-number")]
    [InlineData("marker-block")]
    [InlineData("marker-txhash")]
    [InlineData("marker-txblock")]
    [InlineData("marker-from")]
    [InlineData("marker-to")]
    [InlineData("marker-value")]
    [InlineData("marker-data")]
    public async Task LocalReadinessRequiresManifestCodeHistoryAndInterfaces(string scenario)
    {
        using var local = new LocalFiles();
        if (scenario == "manifest-plan") { local.Document["plans"]!["1"] = 1; local.Save(); }
        await using var rpc = await Rpc.StartAsync((method, parameters) => local.Respond(scenario, method, parameters));
        local.Config.RpcUrl = rpc.Url;
        var verifier = new BlockchainVerifier(Options.Create(local.Config));
        if (scenario == "valid")
        {
            await verifier.CheckConfigurationAsync(1, 5_000_000, default);
            var pending = await verifier.VerifyAsync(new PaymentOrder
            {
                ChainId = 31337, TokenContractAddress = local.Config.TokenAddress, PaymentContractAddress = local.Config.PaymentContractAddress,
                PlanId = 1, Amount = 5_000_000
            }, Hash('c'), default);
            Assert.Equal("Pending", pending.State);
        }
        else Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => verifier.CheckConfigurationAsync(scenario == "manifest-plan-missing" ? 3 : 1, 5_000_000, default))).Status);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-block")]
    [InlineData("block")]
    [InlineData("history-missing-receipt")]
    [InlineData("receipt-status")]
    [InlineData("history-recipient")]
    [InlineData("history-hash")]
    [InlineData("history-block")]
    [InlineData("payment-address")]
    [InlineData("token-address")]
    [InlineData("missing-number")]
    [InlineData("missing-hash")]
    public async Task SqlPaymentAnchorsMustExistInTheLocalChain(string scenario)
    {
        using var local = new LocalFiles();
        var paid = local.Paid();
        if (scenario == "payment-address") paid.PaymentContractAddress = local.Config.TokenAddress;
        if (scenario == "token-address") paid.TokenContractAddress = local.Config.PaymentContractAddress;
        if (scenario == "missing-number") paid.BlockNumber = null;
        if (scenario == "missing-hash") paid.BlockHash = null;
        await using var rpc = await Rpc.StartAsync((method, parameters) => local.Respond(scenario, method, parameters));
        local.Config.RpcUrl = rpc.Url;
        var verifier = new BlockchainVerifier(Options.Create(local.Config));
        if (scenario == "valid") await verifier.CheckHistoryAsync([paid], default);
        else Assert.Contains("підписки збережено", (await Assert.ThrowsAsync<ApiException>(() => verifier.CheckHistoryAsync([paid], default))).Message);
        local.Config.ChainId = 84532;
        await verifier.CheckHistoryAsync([paid], default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-path")]
    [InlineData("large")]
    [InlineData("json")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("testOnly")]
    [InlineData("chainId")]
    [InlineData("tokenDecimals")]
    [InlineData("deploymentBlockNumber")]
    [InlineData("checkpointBlockNumber")]
    [InlineData("plans-null")]
    [InlineData("plans-missing")]
    [InlineData("plans-zero")]
    [InlineData("networkIdentityTransactionHash")]
    [InlineData("networkIdentityData")]
    [InlineData("tokenDeploymentTransactionHash")]
    [InlineData("deploymentTransactionHash")]
    [InlineData("deploymentBlockHash")]
    [InlineData("checkpointBlockHash")]
    [InlineData("tokenRuntimeCodeHash")]
    [InlineData("paymentRuntimeCodeHash")]
    [InlineData("tokenAddress")]
    [InlineData("paymentContractAddress")]
    [InlineData("ownerAddress")]
    public void InvalidLocalManifestCannotEnablePayments(string scenario)
    {
        using var local = new LocalFiles();
        if (scenario == "missing") File.Delete(local.Config.LocalDeploymentPath);
        else if (scenario == "empty-path") local.Config.LocalDeploymentPath = "";
        else if (scenario == "large") File.WriteAllText(local.Config.LocalDeploymentPath, new string('x', 32769));
        else if (scenario == "json") File.WriteAllText(local.Config.LocalDeploymentPath, "{");
        else if (scenario == "null") File.WriteAllText(local.Config.LocalDeploymentPath, "null");
        else
        {
            if (scenario == "testOnly") local.Document[scenario] = false;
            else if (scenario == "plans-null") local.Document["plans"] = null;
            else if (scenario == "plans-missing") local.Document["plans"]!.AsObject().Remove("2");
            else if (scenario == "plans-zero") local.Document["plans"]!["1"] = 0;
            else if (scenario.EndsWith("Hash") || scenario.EndsWith("Address") || scenario.EndsWith("Data")) local.Document[scenario] = "invalid";
            else local.Document[scenario] = 0;
            local.Save();
        }
        Assert.Equal(503, Assert.Throws<ApiException>(() => LocalDeployment.Read(local.Config)).Status);
    }

    [Theory]
    [InlineData("0x", false)]
    [InlineData("valid", true)]
    [InlineData("prefix", false)]
    [InlineData("hex", false)]
    public void ManifestHashesRequireExactly32HexBytes(string value, bool expected)
    {
        var hash = value switch { "valid" => Hash('a'), "prefix" => "ax" + new string('a', 64), "hex" => Hash('g'), _ => value };
        Assert.Equal(expected, LocalDeployment.Hash(hash));
        Assert.Equal("0x56570de287d73cd1cb6092bb8fdee6173974955fdef345ae579ee9f475ea7432", LocalDeployment.CodeHash("0x1234"));
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("decimals")]
    [InlineData("token")]
    [InlineData("payment")]
    public void ExplicitConfigurationMustMatchTheManifest(string scenario)
    {
        using var local = new LocalFiles();
        var deployment = LocalDeployment.Read(local.Config);
        if (scenario == "chain") local.Config.ChainId = 1;
        if (scenario == "decimals") local.Config.TokenDecimals = 18;
        if (scenario == "token") local.Config.TokenAddress = local.Config.PaymentContractAddress;
        if (scenario == "payment") local.Config.PaymentContractAddress = local.Config.TokenAddress;
        Assert.Equal(503, Assert.Throws<ApiException>(() => deployment.Resolve(local.Config)).Status);
    }

    [Fact]
    public async Task ManifestProvidesAddressesDynamicallyWithoutMutatingConfiguration()
    {
        using var local = new LocalFiles();
        var tokenAddress = local.Config.TokenAddress;
        var paymentAddress = local.Config.PaymentContractAddress;
        local.Config.TokenAddress = "";
        local.Config.PaymentContractAddress = "";
        var resolved = local.Config.ResolveContracts();
        Assert.Equal(tokenAddress, resolved.TokenAddress);
        Assert.Equal(paymentAddress, resolved.PaymentContractAddress);
        Assert.Empty(local.Config.TokenAddress);
        await using var factory = new SubscriptionApiFactory(configuration: new Dictionary<string, string?>
        {
            ["Blockchain:ChainId"] = "31337", ["Blockchain:TokenAddress"] = "", ["Blockchain:PaymentContractAddress"] = "",
            ["Blockchain:LocalDeploymentPath"] = local.Config.LocalDeploymentPath,
        });
        using var client = await factory.Client();
        var network = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/network");
        Assert.Equal(tokenAddress, network.GetProperty("tokenAddress").GetString());
        File.Delete(local.Config.LocalDeploymentPath);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/subscriptions/network")).StatusCode);
        var readiness = await client.GetFromJsonAsync<PaymentReadiness>("/api/subscriptions/readiness");
        Assert.Equal("initializing", readiness!.State);
        Assert.False(readiness.Ready);
    }
}

public sealed class LocalPaymentSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableLocalHistoryPreservesConfirmedPaymentAndPro(bool technical)
    {
        await using var db = await SubscriptionTests.Database();
        var (service, order, chain) = await SubscriptionTests.Order(db);
        var payment = await service.SubmitAsync(TestAccount.UserId, order.Id, "0x" + new string('a', 64), default);
        await service.ProcessAsync(payment.Id, default);
        var start = (await db.UserSubscriptions.SingleAsync()).StartDate;
        chain.HistoryError = technical ? new IOException() : new ApiException(503, "Історія Anvil не збігається. Чинні підписки збережено.");
        chain.Evidence = new("Pending");
        await service.ProcessAsync(payment.Id, default);
        Assert.Equal("Confirmed", payment.Status);
        Assert.Equal("Paid", order.Status);
        Assert.Equal("PRO", (await new SubscriptionService(db).GetAsync(TestAccount.UserId, default)).Plan);
        Assert.Equal(start, (await db.UserSubscriptions.SingleAsync()).StartDate);
        Assert.False((await service.ReadinessAsync(default)).Ready);
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(TestAccount.UserId, 1, order.WalletAddress, false, default))).Status);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("disabled")]
    [InlineData("rpc")]
    [InlineData("config")]
    [InlineData("history")]
    public async Task PaymentReadinessHasAnIndependentEndpoint(string scenario)
    {
        var chain = new FakeBlockchain();
        if (scenario == "rpc") chain.ConfigurationError = new IOException();
        if (scenario == "config") chain.ConfigurationError = new ApiException(503, "Контракт відсутній.");
        if (scenario == "history") chain.HistoryError = new ApiException(503, "Історія мережі не збігається.");
        await using var factory = new SubscriptionApiFactory(configuration: new Dictionary<string, string?>
        {
            ["Blockchain:Enabled"] = scenario == "disabled" ? "false" : "true", ["Blockchain:ChainId"] = "31337",
            ["Blockchain:AllowLocalAnvil"] = "true", ["Blockchain:TokenAddress"] = SubscriptionTests.Options().TokenAddress,
            ["Blockchain:PaymentContractAddress"] = SubscriptionTests.Options().PaymentContractAddress,
        }, configureServices: services => services.AddSingleton<IBlockchainVerifier>(chain));
        using var client = await factory.Client();
        var response = await client.GetFromJsonAsync<PaymentReadiness>("/api/subscriptions/readiness");
        Assert.Equal(scenario == "valid", response!.Ready);
        Assert.NotEmpty(response.Message);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/subscriptions/plans")).StatusCode);
    }

    [Fact]
    public async Task SqlGuardRecordsPaymentHistoryAndNeverErasesAnExistingGuard()
    {
        await using var db = await SubscriptionTests.Database();
        using var files = new TestFiles();
        var deployment = Path.Combine(files.Folder, "deployment.json");
        var guard = Path.Combine(files.Folder, "sql-guard.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBlockchainGuard.WriteAsync(db, "", default));
        await LocalBlockchainGuard.WriteAsync(db, deployment, default);
        using (var json = JsonDocument.Parse(File.ReadAllText(guard))) Assert.False(json.RootElement.GetProperty("hasLocalPaymentHistory").GetBoolean());
        var order = new PaymentOrder { UserId = TestAccount.UserId, ChainId = 31337 };
        db.PaymentOrders.Add(order);
        await db.SaveChangesAsync();
        await LocalBlockchainGuard.WriteAsync(db, deployment, default);
        db.PaymentOrders.Remove(order);
        await db.SaveChangesAsync();
        await LocalBlockchainGuard.WriteAsync(db, deployment, default);
        using (var json = JsonDocument.Parse(File.ReadAllText(guard))) Assert.True(json.RootElement.GetProperty("hasLocalPaymentHistory").GetBoolean());
        File.WriteAllText(guard, "broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => LocalBlockchainGuard.WriteAsync(db, deployment, default));
    }
}
