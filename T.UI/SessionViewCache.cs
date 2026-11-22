using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Microsoft.Extensions.DependencyInjection;
using T.UI.ViewModels;
using T.UI.Views.Components;

namespace T.UI;

/// <summary>
/// Resolves and caches a single <see cref="SessionView"/> per <see cref="SessionViewModel"/>.
/// FATabView rebuilds the selected tab's content through the implicit data template on every
/// tab switch; returning the same view instance keeps the live terminal (scrollback and parser
/// state) alive instead of recreating it each time. Cached views are evicted when the owning
/// session is disposed.
/// </summary>
public class SessionViewCache : IDataTemplate
{
    private readonly Dictionary<SessionViewModel, SessionView> _cache = [];

    public Control? Build(object? param)
    {
        if (param is not SessionViewModel vm)
            return null;

        if (_cache.TryGetValue(vm, out var cached))
            return cached;

        var services = ((App)Application.Current!).Services;
        var view = services.GetRequiredService<SessionView>();
        view.DataContext = vm;

        _cache[vm] = view;
        vm.Disposed += OnSessionDisposed;
        return view;
    }

    private void OnSessionDisposed(SessionViewModel vm)
    {
        vm.Disposed -= OnSessionDisposed;
        _cache.Remove(vm);
    }

    public bool Match(object? data) => data is SessionViewModel;
}
