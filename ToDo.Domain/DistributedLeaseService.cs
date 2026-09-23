using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>使用数据库条件更新实现的轻量分布式租约。</summary>
public sealed class DistributedLeaseService
{
    private readonly ApplicationDbContext _context;

    public DistributedLeaseService(ApplicationDbContext context) => _context = context;

    public async Task<DistributedLeaseHandle?> TryAcquireAsync(
        string leaseKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseKey)) throw new ArgumentException("租约键不能为空", nameof(leaseKey));
        var normalizedKey = leaseKey.Trim();
        if (normalizedKey.Length > 120) throw new ArgumentException("租约键不能超过 120 个字符", nameof(leaseKey));
        var now = AppTime.Now;
        var ownerId = Guid.NewGuid().ToString("N");
        var expiresAt = now.Add(duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : duration);

        var renewed = await _context.BackgroundJobLeases
            .Where(item => item.LeaseKey == normalizedKey && item.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OwnerId, ownerId)
                .SetProperty(item => item.AcquiredAt, now)
                .SetProperty(item => item.ExpiresAt, expiresAt), cancellationToken);
        if (renewed == 1) return new DistributedLeaseHandle(this, normalizedKey, ownerId);

        if (await _context.BackgroundJobLeases.AsNoTracking()
            .AnyAsync(item => item.LeaseKey == normalizedKey, cancellationToken))
            return null;

        var lease = new BackgroundJobLease
        {
            LeaseKey = normalizedKey,
            OwnerId = ownerId,
            AcquiredAt = now,
            ExpiresAt = expiresAt
        };
        _context.BackgroundJobLeases.Add(lease);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new DistributedLeaseHandle(this, normalizedKey, ownerId);
        }
        catch (DbUpdateException)
        {
            _context.Entry(lease).State = EntityState.Detached;
            return null;
        }
    }

    private async ValueTask ReleaseAsync(string leaseKey, string ownerId)
    {
        await _context.BackgroundJobLeases
            .Where(item => item.LeaseKey == leaseKey && item.OwnerId == ownerId)
            .ExecuteDeleteAsync();
        var tracked = _context.ChangeTracker.Entries<BackgroundJobLease>()
            .FirstOrDefault(entry => entry.Entity.LeaseKey == leaseKey);
        if (tracked != null) tracked.State = EntityState.Detached;
    }

    public sealed class DistributedLeaseHandle : IAsyncDisposable
    {
        private readonly DistributedLeaseService _service;
        private readonly string _leaseKey;
        private readonly string _ownerId;
        private bool _released;

        internal DistributedLeaseHandle(DistributedLeaseService service, string leaseKey, string ownerId)
        {
            _service = service;
            _leaseKey = leaseKey;
            _ownerId = ownerId;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;
            await _service.ReleaseAsync(_leaseKey, _ownerId);
        }
    }
}
