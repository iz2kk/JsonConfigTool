using System.Collections.Concurrent;

namespace ConfigTool.Services;

public sealed class ConfigToolOperationCancellationService
{
    private readonly ConcurrentDictionary<string, OperationLease> _operations = new(StringComparer.OrdinalIgnoreCase);

    public OperationLease Begin(string connectionId, string scope, CancellationToken parentToken)
    {
        connectionId = NormalizeConnectionId(connectionId);
        scope = NormalizeScope(scope);
        var key = BuildKey(connectionId, scope);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var lease = new OperationLease(this, key, connectionId, scope, linked);

        if (_operations.TryRemove(key, out var existing))
        {
            existing.Cancel("Một hành động mới đã thay thế hành động cũ trong cùng tab.");
            existing.Dispose();
        }

        _operations[key] = lease;
        return lease;
    }

    public int Cancel(string connectionId, string scope, string? reason = null)
    {
        connectionId = NormalizeConnectionId(connectionId);
        scope = NormalizeScope(scope);
        var key = BuildKey(connectionId, scope);
        if (!_operations.TryGetValue(key, out var lease))
        {
            return 0;
        }

        lease.Cancel(reason ?? "Người dùng đã hủy hành động.");
        return 1;
    }

    public int CancelByPrefix(string connectionId, string scopePrefix, string? reason = null)
    {
        connectionId = NormalizeConnectionId(connectionId);
        scopePrefix = NormalizeScope(scopePrefix);
        var prefix = BuildKey(connectionId, scopePrefix);
        var count = 0;
        foreach (var pair in _operations.ToArray())
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pair.Value.Cancel(reason ?? "Người dùng đã hủy hành động.");
            count++;
        }

        return count;
    }

    public int CancelAll(string connectionId, string? reason = null)
    {
        connectionId = NormalizeConnectionId(connectionId);
        var prefix = NormalizeConnectionId(connectionId) + ":";
        var count = 0;
        foreach (var pair in _operations.ToArray())
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pair.Value.Cancel(reason ?? "Người dùng đã hủy tất cả hành động.");
            count++;
        }

        return count;
    }

    private void Complete(OperationLease lease)
    {
        _operations.TryRemove(lease.Key, out _);
    }

    private static string BuildKey(string connectionId, string scope) => connectionId + ":" + scope;

    private static string NormalizeConnectionId(string? connectionId) => string.IsNullOrWhiteSpace(connectionId) ? "unknown" : connectionId.Trim();

    private static string NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? "default" : scope.Trim().Replace(' ', '_').ToLowerInvariant();

    public sealed class OperationLease : IDisposable
    {
        private readonly ConfigToolOperationCancellationService _owner;
        private readonly CancellationTokenSource _cts;
        private int _disposed;

        internal OperationLease(ConfigToolOperationCancellationService owner, string key, string connectionId, string scope, CancellationTokenSource cts)
        {
            _owner = owner;
            Key = key;
            ConnectionId = connectionId;
            Scope = scope;
            _cts = cts;
            StartedAt = DateTimeOffset.Now;
        }

        public string Key { get; }
        public string ConnectionId { get; }
        public string Scope { get; }
        public DateTimeOffset StartedAt { get; }
        public string? CancelReason { get; private set; }
        public CancellationToken Token => _cts.Token;

        internal void Cancel(string reason)
        {
            CancelReason = reason;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation already completed.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Complete(this);
            _cts.Dispose();
        }
    }
}
