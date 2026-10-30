using System;
using System.Threading;
using System.Threading.Tasks;

namespace QQDatabaseExplorer.Services;

internal sealed class QQNtDatabaseQueryGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Run(Action query, CancellationToken cancellationToken = default)
    {
        Run(() =>
        {
            query();
            return true;
        }, cancellationToken);
    }

    public T Run<T>(Func<T> query, CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return query();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<T> query, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(query, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
