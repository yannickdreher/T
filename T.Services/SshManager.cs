using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Models;

namespace T.Services;

/// <summary>
/// Factory and registry for SshService instances keyed by session ID.
/// Registered as singleton in the DI container so all ViewModels share one registry.
/// </summary>
public sealed class SshManager(IServiceProvider serviceProvider) : ISshManager, IDisposable
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Dictionary<string, ISshService> _instances = [];
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public ISshService Create(SshSession session, uint cols, uint rows, uint pixelWidth, uint pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            if (_instances.TryGetValue(session.Id, out var existing))
                existing.Dispose();

            var svc = ActivatorUtilities.CreateInstance<SshService>(
                _serviceProvider,
                session,
                cols,
                rows,
                pixelWidth,
                pixelHeight);

            _instances[session.Id] = svc;
            return svc;
        }
    }

    /// <inheritdoc/>
    public void Release(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        lock (_lock)
        {
            if (_instances.TryGetValue(sessionId, out var svc))
            {
                svc.Dispose();
                _instances.Remove(sessionId);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var svc in _instances.Values)
                svc.Dispose();
            _instances.Clear();
        }
    }
}