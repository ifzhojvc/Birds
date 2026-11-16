using System.Text.Json;
using BirdLab.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BirdLab.Api.Subscriptions;

public sealed record LocalOrderAddress(string TokenAddress, string PaymentContractAddress);
public sealed record LocalConfirmedPayment(string TransactionHash, long? BlockNumber, string? BlockHash, string TokenAddress, string PaymentContractAddress);
public sealed class LocalSqlGuard
{
    public bool HasLocalPaymentHistory { get; set; }
    public LocalOrderAddress[] OrdersAddresses { get; set; } = [];
    public LocalConfirmedPayment[] ConfirmedPayments { get; set; } = [];
}

public static class LocalBlockchainGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static async Task WriteAsync(BirdDbContext db, string deploymentPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(deploymentPath)) throw new InvalidOperationException("Blockchain:LocalDeploymentPath is required for the local SQL guard.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(deploymentPath))!;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "sql-guard.json");
        var previous = File.Exists(target) ? JsonSerializer.Deserialize<LocalSqlGuard>(await File.ReadAllTextAsync(target, token), JsonOptions) ?? throw new JsonException() : new LocalSqlGuard();
        var orders = await db.PaymentOrders.AsNoTracking().Where(o => o.ChainId == 31337).Select(o => new LocalOrderAddress(o.TokenContractAddress, o.PaymentContractAddress)).ToArrayAsync(token);
        var payments = await db.CryptoPayments.AsNoTracking().Where(p => p.ChainId == 31337).Select(p => new LocalOrderAddress(p.TokenContractAddress, p.PaymentContractAddress)).ToArrayAsync(token);
        var confirmed = await db.CryptoPayments.AsNoTracking().Where(p => p.ChainId == 31337 && p.ConfirmedAt != null).Select(p => new LocalConfirmedPayment(p.TransactionHash, p.BlockNumber, p.BlockHash, p.TokenContractAddress, p.PaymentContractAddress)).ToArrayAsync(token);
        var state = new LocalSqlGuard
        {
            HasLocalPaymentHistory = previous.HasLocalPaymentHistory || orders.Length > 0 || payments.Length > 0,
            OrdersAddresses = previous.OrdersAddresses.Concat(orders).Concat(payments).Distinct().ToArray(),
            ConfirmedPayments = previous.ConfirmedPayments.Concat(confirmed).Distinct().ToArray()
        };
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), token);
        File.Move(temporary, target, true);
    }
}
