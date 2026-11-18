using System.Text.Json;
using BirdLab.Api.Infrastructure;
using Nethereum.Util;

namespace BirdLab.Api.Subscriptions;

public sealed class LocalDeployment
{
    public Guid? TransitionId { get; set; }
    public string? SourceBackupSha256 { get; set; }
    public string? SourceFingerprint { get; set; }
    public Guid NetworkInstanceId { get; set; }
    public int Version { get; set; }
    public bool TestOnly { get; set; }
    public long ChainId { get; set; }
    public string TokenAddress { get; set; } = "";
    public string PaymentContractAddress { get; set; } = "";
    public string OwnerAddress { get; set; } = "";
    public int TokenDecimals { get; set; }
    public string TokenDeploymentTransactionHash { get; set; } = "";
    public string DeploymentTransactionHash { get; set; } = "";
    public long DeploymentBlockNumber { get; set; }
    public string DeploymentBlockHash { get; set; } = "";
    public string NetworkIdentityTransactionHash { get; set; } = "";
    public string NetworkIdentityData { get; set; } = "";
    public long CheckpointBlockNumber { get; set; }
    public string CheckpointBlockHash { get; set; } = "";
    public string TokenRuntimeCodeHash { get; set; } = "";
    public string PaymentRuntimeCodeHash { get; set; } = "";
    public Dictionary<int, long> Plans { get; set; } = [];

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static bool Hash(string? value) => value is { Length: 66 } && value.StartsWith("0x", StringComparison.Ordinal) && value[2..].All(Uri.IsHexDigit);
    public static string CodeHash(string code) => "0x" + Convert.ToHexString(new Sha3Keccack().CalculateHash(Convert.FromHexString(code[2..]))).ToLowerInvariant();

    public static LocalDeployment Read(BlockchainOptions config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.LocalDeploymentPath)) throw new IOException();
            var file = new FileInfo(config.LocalDeploymentPath);
            if (!file.Exists || file.Length > 32768) throw new IOException();
            var result = JsonSerializer.Deserialize<LocalDeployment>(File.ReadAllText(file.FullName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException();
            if (result.NetworkInstanceId == Guid.Empty || result.Version != 1 || !result.TestOnly || result.ChainId != 31337 || result.TokenDecimals != 6 || result.DeploymentBlockNumber < 1 || result.CheckpointBlockNumber < result.DeploymentBlockNumber || result.Plans is null || !result.Plans.ContainsKey(1) || !result.Plans.ContainsKey(2) || result.Plans.Values.Any(v => v <= 0) || !new[] { result.NetworkIdentityTransactionHash, result.NetworkIdentityData, result.TokenDeploymentTransactionHash, result.DeploymentTransactionHash, result.DeploymentBlockHash, result.CheckpointBlockHash, result.TokenRuntimeCodeHash, result.PaymentRuntimeCodeHash }.All(Hash)) throw new JsonException();
            _ = WalletService.Address(result.TokenAddress);
            _ = WalletService.Address(result.PaymentContractAddress);
            _ = WalletService.Address(result.OwnerAddress);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ApiException or ArgumentException)
        {
            throw new ApiException(503, "Локальні платежі не готові: перевірений запис розгортання Anvil відсутній або пошкоджений. Перевірте blockchain-init; історію та підписки збережено.", "initializing");
        }
    }

    public BlockchainOptions Resolve(BlockchainOptions configured)
    {
        if (configured.NetworkInstanceId.HasValue && configured.NetworkInstanceId != NetworkInstanceId || configured.ChainId != ChainId || configured.TokenDecimals != TokenDecimals ||
            (!string.IsNullOrEmpty(configured.TokenAddress) && !Same(configured.TokenAddress, TokenAddress)) ||
            (!string.IsNullOrEmpty(configured.PaymentContractAddress) && !Same(configured.PaymentContractAddress, PaymentContractAddress)))
            throw new ApiException(503, "Адреси або параметри Anvil не збігаються із серверною конфігурацією. Автоматичну оплату зупинено.", "configuration_mismatch");
        return configured.WithContracts(TokenAddress, PaymentContractAddress, NetworkInstanceId);
    }
}
