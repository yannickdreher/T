using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Models;

namespace T.Services;

/// <summary>
/// Factory and registry of the open <see cref="SshService"/> connections. Each tab gets its own
/// connection, so the same saved session can be open several times. Registered as singleton;
/// disposing it (on exit) closes every connection that is still open.
/// </summary>
public sealed class SshManager(IServiceProvider serviceProvider) : ISshManager, IDisposable
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly HashSet<ISshService> _instances = [];
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public ISshService Create(SshSession session, uint cols, uint rows, uint pixelWidth, uint pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(session);

        var service = ActivatorUtilities.CreateInstance<SshService>(
            _serviceProvider,
            session,
            cols,
            rows,
            pixelWidth,
            pixelHeight);

        lock (_lock)
            _instances.Add(service);
        return service;
    }

    /// <inheritdoc/>
    public void Release(ISshService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (_lock)
            _instances.Remove(service);
        service.Dispose();
    }

    public void Dispose()
    {
        List<ISshService> open;
        lock (_lock)
        {
            open = [.. _instances];
            _instances.Clear();
        }
        foreach (var service in open)
            service.Dispose();
    }
}
