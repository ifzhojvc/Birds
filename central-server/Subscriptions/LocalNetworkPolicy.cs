using System.Text.Json;
using BirdLab.Api.Data;
using BirdLab.Api.Infrastructure;
using BirdLab.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirdLab.Api.Subscriptions;

public static class LocalNetworkPolicy
{
    public static async Task<LocalNetworkInstance?> RequireCurrentAsync(BirdDbContext db, BlockchainOptions config, CancellationToken token)
    {
        if (config.ChainId != 31337) return null;
        var resolved = config.ResolveContracts();
        var current = await db.LocalNetworkInstances.AsNoTracking().SingleOrDefaultAsync(n => n.IsCurrent, token);
        if (current is null || current.Phase != "Deployed" || resolved.NetworkInstanceId is null || current.Id != resolved.NetworkInstanceId)
            throw new ApiException(503, "Локальну мережу ще не прив’язано до перевіреного екземпляра. Історичні платежі та права збережено.", "network_instance_unbound");
        if (await db.PaymentOrders.AnyAsync(o => o.ChainId == 31337 && o.NetworkInstanceId == null, token) || await db.CryptoPayments.AnyAsync(p => p.ChainId == 31337 && p.NetworkInstanceId == null, token))
            throw new ApiException(503, "Історія містить нерозподілені локальні платежі. Потрібне явне узгодження переходу між мережами.", "legacy_history_unassigned");
        if (await db.CryptoPayments.AnyAsync(p => p.ChainId == 31337 && !db.PaymentOrders.Any(o => o.Id == p.PaymentOrderId && o.NetworkInstanceId == p.NetworkInstanceId), token))
            throw new ApiException(503, "Платіж і замовлення пов’язані з різними екземплярами мережі.", "network_instance_mismatch");
        if (!string.IsNullOrWhiteSpace(config.LocalDeploymentPath))
        {
            var deployment = LocalDeployment.Read(config);
            var canonical = JsonSerializer.Serialize(deployment);
            if (current.DeploymentJson != canonical) throw new ApiException(503, "Manifest не відповідає екземпляру мережі, прив’язаному до SQL.", "network_instance_mismatch");
        }
        return current;
    }

    public static async Task<string> VerificationStateAsync(BirdDbContext db, long chainId, Guid? instanceId, CancellationToken token)
    {
        if (chainId != 31337) return "Deployed";
        if (instanceId is null) return "UnassignedLegacy";
        return await db.LocalNetworkInstances.AsNoTracking().Where(n => n.Id == instanceId).Select(n => n.Phase).SingleOrDefaultAsync(token) ?? "Unknown";
    }

    public static bool Historical(string state) => state is "ArchivedUnverified" or "UnassignedLegacy";
}
