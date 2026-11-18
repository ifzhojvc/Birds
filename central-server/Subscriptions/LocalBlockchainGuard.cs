using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirdLab.Api.Subscriptions;

public sealed record LocalOrderAddress(string TokenAddress, string PaymentContractAddress);
public sealed record LocalConfirmedPayment(string TransactionHash, long? BlockNumber, string? BlockHash, string TokenAddress, string PaymentContractAddress);
public sealed class LocalSqlGuard
{
    public int Version { get; set; } = 1;
    public bool HasLocalPaymentHistory { get; set; }
    public LocalOrderAddress[] OrdersAddresses { get; set; } = [];
    public LocalConfirmedPayment[] ConfirmedPayments { get; set; } = [];
}

public static class LocalBlockchainGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static (LocalOrderAddress[] Orders, LocalConfirmedPayment[] Payments, bool History) Previous(JsonElement previous)
    {
        if (previous.ValueKind == JsonValueKind.Undefined) return ([], [], false);
        if (previous.TryGetProperty("version", out var declaredVersion) && declaredVersion.GetInt32() is not (1 or 2)) throw new InvalidOperationException("Unknown SQL guard version; automatic downgrade is forbidden.");
        var history = previous.GetProperty("hasLocalPaymentHistory").GetBoolean();
        var sections = new List<JsonElement>();
        if (previous.TryGetProperty("version", out var version) && version.GetInt32() == 2)
        {
            sections.Add(previous.GetProperty("activeNetwork"));
            sections.AddRange(previous.GetProperty("archivedNetworks").EnumerateArray());
        }
        else sections.Add(previous);
        var orders = sections.SelectMany(s => s.TryGetProperty("ordersAddresses", out var values) ? JsonSerializer.Deserialize<LocalOrderAddress[]>(values.GetRawText(), JsonOptions) ?? [] : []).ToArray();
        var payments = sections.SelectMany(s => s.TryGetProperty("confirmedPayments", out var values) ? JsonSerializer.Deserialize<LocalConfirmedPayment[]>(values.GetRawText(), JsonOptions) ?? [] : []).ToArray();
        return (orders, payments, history);
    }

    public static async Task WriteAsync(BirdDbContext db, string deploymentPath, CancellationToken token, Guid? requestedInstanceId = null)
    {
        if (string.IsNullOrWhiteSpace(deploymentPath)) throw new InvalidOperationException("Blockchain:LocalDeploymentPath is required for the local SQL guard.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(deploymentPath))!;
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "sql-guard.json");
        using var saved = File.Exists(target) ? JsonDocument.Parse(await File.ReadAllTextAsync(target, token)) : null;
        var previous = Previous(saved?.RootElement ?? default);
        var orders = await db.PaymentOrders.AsNoTracking().Where(o => o.ChainId == 31337).ToArrayAsync(token);
        var payments = await db.CryptoPayments.AsNoTracking().Where(p => p.ChainId == 31337).ToArrayAsync(token);
        var allAddresses = orders.Select(o => new LocalOrderAddress(o.TokenContractAddress, o.PaymentContractAddress)).Concat(payments.Select(p => new LocalOrderAddress(p.TokenContractAddress, p.PaymentContractAddress))).Distinct().ToArray();
        var allConfirmed = payments.Where(p => p.ConfirmedAt != null).Select(p => new LocalConfirmedPayment(p.TransactionHash, p.BlockNumber, p.BlockHash, p.TokenContractAddress, p.PaymentContractAddress)).Distinct().ToArray();
        var history = previous.History || orders.Length > 0 || payments.Length > 0;
        var networks = await db.LocalNetworkInstances.AsNoTracking().ToArrayAsync(token);
        object output;
        if (networks.Length == 0 && saved?.RootElement.TryGetProperty("version", out var savedVersion) == true && savedVersion.GetInt32() == 2) throw new InvalidOperationException("SQL lost versioned instance metadata; automatic downgrade is forbidden.");
        if (networks.Length == 0 && history)
        {
            output = new LocalSqlGuard { HasLocalPaymentHistory = true, OrdersAddresses = previous.Orders.Concat(allAddresses).Distinct().ToArray(), ConfirmedPayments = previous.Payments.Concat(allConfirmed).Distinct().ToArray() };
        }
        else
        {
            if (networks.Length == 0)
            {
                if (saved?.RootElement.TryGetProperty("version", out var previousVersion) == true && previousVersion.GetInt32() == 2) throw new InvalidOperationException("SQL lost the local instance metadata; automatic replacement is forbidden.");
                var fresh = new LocalNetworkInstance { Id = requestedInstanceId ?? Guid.NewGuid(), IsCurrent = true, Phase = "Planned" };
                if (fresh.Id == Guid.Empty) throw new InvalidOperationException("The planned local network identity is invalid.");
                db.LocalNetworkInstances.Add(fresh);
                await db.SaveChangesAsync(token);
                networks = [fresh];
            }
            var current = networks.SingleOrDefault(n => n.IsCurrent) ?? throw new InvalidOperationException("There is no unambiguous current local instance.");
            if (requestedInstanceId is not null && requestedInstanceId != current.Id) throw new InvalidOperationException("Configured local instance differs from the SQL guard.");
            if (orders.Any(o => o.NetworkInstanceId is null || networks.All(n => n.Id != o.NetworkInstanceId)) || payments.Any(p => p.NetworkInstanceId is null || networks.All(n => n.Id != p.NetworkInstanceId))) throw new InvalidOperationException("Unassigned local history blocks the versioned guard.");
            if (previous.Orders.Except(allAddresses).Any() || previous.Payments.Except(allConfirmed).Any()) throw new InvalidOperationException("The monotonic guard contains evidence missing from SQL. Automatic history removal is forbidden.");
            var batch = current.TransitionBatchId is { } batchId ? await db.LocalTransitionBatches.AsNoTracking().SingleAsync(b => b.Id == batchId, token) : null;
            if (batch is not null && batch.TargetNetworkInstanceId != current.Id) throw new InvalidOperationException("The planned instance differs from the approved transition.");
            object Section(LocalNetworkInstance network) => new
            {
                networkInstanceId = network.Id, phase = network.Phase, chainId = network.ChainId,
                ordersAddresses = orders.Where(o => o.NetworkInstanceId == network.Id).Select(o => new LocalOrderAddress(o.TokenContractAddress, o.PaymentContractAddress)).Concat(payments.Where(p => p.NetworkInstanceId == network.Id).Select(p => new LocalOrderAddress(p.TokenContractAddress, p.PaymentContractAddress))).Distinct().ToArray(),
                confirmedPayments = payments.Where(p => p.NetworkInstanceId == network.Id && p.ConfirmedAt != null).Select(p => new LocalConfirmedPayment(p.TransactionHash, p.BlockNumber, p.BlockHash, p.TokenContractAddress, p.PaymentContractAddress)).Distinct().ToArray()
            };
            var active = JsonSerializer.SerializeToElement(Section(current), JsonOptions);
            var authorization = batch is null ? null : new { backupSha256 = batch.BackupSha256, legacyArchived = true, transitionId = batch.Id, sourceFingerprint = batch.SourceFingerprint };
            output = new
            {
                version = 2, hasLocalPaymentHistory = history,
                activeNetwork = new
                {
                    networkInstanceId = current.Id, phase = current.Phase, chainId = current.ChainId, authorization,
                    deployment = current.DeploymentJson is null ? null : JsonSerializer.Deserialize<LocalDeployment>(current.DeploymentJson, JsonOptions),
                    ordersAddresses = active.GetProperty("ordersAddresses"), confirmedPayments = active.GetProperty("confirmedPayments")
                },
                archivedNetworks = networks.Where(n => !n.IsCurrent).Select(Section).ToArray()
            };
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(output, JsonOptions), token);
        File.Move(temporary, target, true);
    }
}
