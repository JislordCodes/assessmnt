namespace ExpenseClassifier.Services;

/// <summary>
/// Async mutual exclusion per string key. Used for cache-stampede protection: concurrent requests for the
/// same expense wait for the first one to populate the cache instead of all calling the LLM.
/// Each waiter uses its own cancellation token, so one caller cancelling never fails the others.
/// Entries are reference counted and removed when unused, so the dictionary cannot grow unbounded.
/// </summary>
public sealed class KeyedAsyncLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Release(string key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_entries)
        {
            if (--entry.RefCount == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, entry, held: true);
            }
        }
    }
}
