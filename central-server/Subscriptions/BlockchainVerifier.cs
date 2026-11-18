using System.Numerics;
using BirdLab.Api.Models;
using BirdLab.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Hex.HexTypes;

namespace BirdLab.Api.Subscriptions;

[Event("SubscriptionPurchased")]
public sealed class SubscriptionPurchasedEvent : IEventDTO
{
    [Parameter("bytes32", "orderId", 1, true)] public byte[] OrderId { get; set; } = [];
    [Parameter("address", "payer", 2, true)] public string Payer { get; set; } = "";
    [Parameter("uint256", "planId", 3, true)] public BigInteger PlanId { get; set; }
    [Parameter("uint256", "amount", 4, false)] public BigInteger Amount { get; set; }
    [Parameter("address", "token", 5, false)] public string Token { get; set; } = "";
}
[Function("prices", "uint256")]
public sealed class PriceFunction : FunctionMessage
{
    [Parameter("uint256", "", 1)] public BigInteger PlanId { get; set; }
}
[Function("paymentToken", "address")] public sealed class TokenFunction : FunctionMessage;
[Function("decimals", "uint8")] public sealed class DecimalsFunction : FunctionMessage;
[Function("paused", "bool")] public sealed class PausedFunction : FunctionMessage;
[Function("owner", "address")] public sealed class OwnerFunction : FunctionMessage;

public sealed record PaymentEvidence(string State, string? Error = null, long? BlockNumber = null, string? BlockHash = null, int? LogIndex = null);
public interface IBlockchainVerifier
{
    Task CheckConfigurationAsync(int planId, long price, CancellationToken token);
    Task CheckHistoryAsync(IReadOnlyCollection<CryptoPayment> history, CancellationToken token);
    Task<PaymentEvidence> VerifyAsync(PaymentOrder order, string transactionHash, CancellationToken token);
}

public sealed class BlockchainVerifier(IOptions<BlockchainOptions> settings) : IBlockchainVerifier
{
    private (BlockchainOptions Config, LocalDeployment? Deployment) Configuration()
    {
        var config = settings.Value;
        var deployment = config.ChainId == 31337 ? LocalDeployment.Read(config) : null;
        config = deployment?.Resolve(config) ?? config;
        config.RequirePayments();
        return (config, deployment);
    }
    private static Task<T> Rpc<T>(Task<T> task, CancellationToken token) => task.WaitAsync(TimeSpan.FromSeconds(12), token);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ApiException HistoryMismatch() => new(503, "Історія локального Anvil не збігається зі збереженими платежами. Платежі зупинено; чинні підписки збережено. Відновіть попередній стан мережі.", "history_mismatch");

    private static ApiException ContractMismatch() => new(503, "Контракти Anvil не відповідають перевіреному розгортанню. Платежі зупинено.", "contract_mismatch");

    private static async Task CheckLocalAsync(Web3 web3, LocalDeployment deployment, string tokenCode, string paymentCode, CancellationToken token)
    {
        if (!Same(LocalDeployment.CodeHash(tokenCode), deployment.TokenRuntimeCodeHash) || !Same(LocalDeployment.CodeHash(paymentCode), deployment.PaymentRuntimeCodeHash)) throw ContractMismatch();
        foreach (var anchor in new[] { (deployment.DeploymentBlockNumber, deployment.DeploymentBlockHash), (deployment.CheckpointBlockNumber, deployment.CheckpointBlockHash) })
        {
            var block = await Rpc(web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(anchor.Item1))), token);
            if (block is null || !Same(block.BlockHash, anchor.Item2)) throw HistoryMismatch();
        }
        foreach (var deploymentReceipt in new[] { (deployment.TokenDeploymentTransactionHash, deployment.TokenAddress), (deployment.DeploymentTransactionHash, deployment.PaymentContractAddress) })
        {
            var receipt = await Rpc(web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(deploymentReceipt.Item1), token);
            if (receipt is null || receipt.Status?.Value != 1 || receipt.BlockNumber is null || !Same(receipt.TransactionHash, deploymentReceipt.Item1) || !Same(receipt.ContractAddress, deploymentReceipt.Item2)) throw HistoryMismatch();
            var block = await Rpc(web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(receipt.BlockNumber)), token);
            if (block is null || !Same(block.BlockHash, receipt.BlockHash)) throw HistoryMismatch();
        }
        var identity = await Rpc(web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(deployment.NetworkIdentityTransactionHash), token);
        var identityReceipt = await Rpc(web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(deployment.NetworkIdentityTransactionHash), token);
        if (identity is null || identityReceipt is null || identityReceipt.Status?.Value != 1 || identityReceipt.BlockNumber?.Value != deployment.CheckpointBlockNumber || !Same(identityReceipt.BlockHash, deployment.CheckpointBlockHash) || !Same(identity.TransactionHash, deployment.NetworkIdentityTransactionHash) || !Same(identity.BlockHash, deployment.CheckpointBlockHash) || !Same(identity.From, deployment.OwnerAddress) || !Same(identity.To, deployment.OwnerAddress) || identity.Value?.Value != 0 || !Same(identity.Input, deployment.NetworkIdentityData)) throw HistoryMismatch();
        var owner = await Rpc(web3.Eth.GetContractHandler(deployment.PaymentContractAddress).QueryAsync<OwnerFunction, string>(new()), token);
        if (!Same(owner, deployment.OwnerAddress)) throw ContractMismatch();
    }

    public async Task CheckConfigurationAsync(int planId, long price, CancellationToken token)
    {
        var (config, deployment) = Configuration();
        var web3 = new Web3(config.RpcUrl);
        if ((await Rpc(web3.Eth.ChainId.SendRequestAsync(), token)).Value != config.ChainId) throw new ApiException(503, "RPC підключений до іншої мережі.", "wrong_chain");
        var code = await Rpc(web3.Eth.GetCode.SendRequestAsync(config.PaymentContractAddress), token);
        var tokenCode = await Rpc(web3.Eth.GetCode.SendRequestAsync(config.TokenAddress), token);
        if (code is "0x" or "0x0" or "" || tokenCode is "0x" or "0x0" or "") throw new ApiException(503, "Платіжний контракт або токен відсутній у вибраній мережі.", "contracts_missing");
        if (deployment is not null)
        {
            await CheckLocalAsync(web3, deployment, tokenCode, code, token);
            if (!deployment.Plans.TryGetValue(planId, out var deployedPrice) || deployedPrice != price) throw new ApiException(503, "Локальний тариф не відповідає перевіреному розгортанню Anvil.", "contract_mismatch");
        }
        var contract = web3.Eth.GetContractHandler(config.PaymentContractAddress);
        var address = await Rpc(contract.QueryAsync<TokenFunction, string>(new()), token);
        var amount = await Rpc(contract.QueryAsync<PriceFunction, BigInteger>(new() { PlanId = planId }), token);
        var paused = await Rpc(contract.QueryAsync<PausedFunction, bool>(new()), token);
        var decimals = await Rpc(web3.Eth.GetContractHandler(config.TokenAddress).QueryAsync<DecimalsFunction, byte>(new()), token);
        if (!Same(address, config.TokenAddress) || amount != price || decimals != config.TokenDecimals || paused)
            throw new ApiException(503, "Ціна, токен або стан контракту не відповідає серверному тарифу. Потрібна перевірка адміністратора.", "contract_mismatch");
    }

    public async Task CheckHistoryAsync(IReadOnlyCollection<CryptoPayment> history, CancellationToken token)
    {
        if (settings.Value.ChainId != 31337) return;
        var (config, deployment) = Configuration();
        var web3 = new Web3(config.RpcUrl);
        foreach (var payment in history)
        {
            if (payment.NetworkInstanceId != deployment!.NetworkInstanceId || !Same(payment.PaymentContractAddress, config.PaymentContractAddress) || !Same(payment.TokenContractAddress, config.TokenAddress) || payment.BlockNumber is null || !LocalDeployment.Hash(payment.BlockHash)) throw HistoryMismatch();
            var block = await Rpc(web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(payment.BlockNumber.Value))), token);
            var receipt = await Rpc(web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(payment.TransactionHash), token);
            if (block is null || receipt is null || receipt.Status?.Value != 1 || !Same(block.BlockHash, payment.BlockHash) || !Same(receipt.BlockHash, payment.BlockHash) || !Same(receipt.TransactionHash, payment.TransactionHash) || !Same(receipt.To, config.PaymentContractAddress)) throw HistoryMismatch();
        }
    }

    public async Task<PaymentEvidence> VerifyAsync(PaymentOrder order, string transactionHash, CancellationToken token)
    {
        var (config, deployment) = Configuration();
        if (order.ChainId != config.ChainId || !Same(order.TokenContractAddress, config.TokenAddress) || !Same(order.PaymentContractAddress, config.PaymentContractAddress)) return new("Rejected", "Заказ не відповідає налаштованій тестовій мережі.");
        if (deployment is not null && order.NetworkInstanceId != deployment.NetworkInstanceId) return new("RpcUnavailable", "Замовлення належить іншому екземпляру локальної мережі.");
        var web3 = new Web3(config.RpcUrl);
        if ((await Rpc(web3.Eth.ChainId.SendRequestAsync(), token)).Value != order.ChainId) return new("RpcUnavailable", "RPC повернув іншу мережу.");
        if (deployment is not null) await CheckConfigurationAsync(order.PlanId, order.Amount, token);
        var transaction = await Rpc(web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(transactionHash), token);
        if (transaction is null) return new("Pending", "Транзакцію ще не знайдено через RPC.");
        if (!Same(transaction.TransactionHash, transactionHash) || !Same(transaction.To, order.PaymentContractAddress) || !Same(transaction.From, order.WalletAddress) || transaction.Value.Value != 0)
            return new("Rejected", "Транзакція має іншого відправника або одержувача.");
        var selector = new Sha3Keccack().CalculateHash("purchaseSubscription(bytes32,uint256)")[..8];
        var expectedInput = "0x" + selector + order.OrderId[2..] + order.PlanId.ToString("x").PadLeft(64, '0');
        if (!Same(transaction.Input, expectedInput)) return new("Rejected", "Виклик контракту не відповідає замовленню.");
        var receipt = await Rpc(web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(transactionHash), token);
        if (receipt is null) return new("Pending", "Очікуємо включення транзакції до блоку.");
        if (receipt.Status?.Value != 1) return new("Rejected", "Транзакція завершилася помилкою у блокчейні.");
        if (!Same(receipt.To, order.PaymentContractAddress) || !Same(receipt.TransactionHash, transactionHash) || !Same(receipt.From, order.WalletAddress)) return new("Rejected", "Receipt не відповідає транзакції.");
        var block = await Rpc(web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(receipt.BlockNumber)), token);
        if (block is null || !Same(block.BlockHash, receipt.BlockHash) || !Same(transaction.BlockHash, receipt.BlockHash)) return new("Reorged", "Блок більше не належить канонічному ланцюжку.");
        var minedAt = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value);
        if (minedAt < order.CreatedAt.AddSeconds(-30) || minedAt > order.ExpiresAt) return new("Rejected", "Транзакцію включено до блоку поза строком замовлення.");
        var events = receipt.DecodeAllEvents<SubscriptionPurchasedEvent>().Where(e => Same(e.Log.Address, order.PaymentContractAddress) && Same("0x" + Convert.ToHexString(e.Event.OrderId), order.OrderId)).ToArray();
        if (events.Length != 1) return new("Rejected", "Не знайдено єдину справжню подію оплати замовлення.");
        var purchased = events[0];
        if (!Same(purchased.Event.Payer, order.WalletAddress) || purchased.Event.PlanId != order.PlanId || purchased.Event.Amount != order.Amount || !Same(purchased.Event.Token, order.TokenContractAddress))
            return new("Rejected", "Параметри події не відповідають замовленню, токену або сумі.");
        var latest = (await Rpc(web3.Eth.Blocks.GetBlockNumber.SendRequestAsync(), token)).Value;
        var confirmations = latest - receipt.BlockNumber.Value + 1;
        var state = confirmations >= config.Confirmations ? "Confirmed" : "Pending";
        return new(state, state == "Pending" ? $"Очікуємо {config.Confirmations} підтверджень." : null, (long)receipt.BlockNumber.Value, receipt.BlockHash.ToLowerInvariant(), (int)purchased.Log.LogIndex.Value);
    }
}
