using Custodex.Studio.Views;

namespace Custodex.Studio.Tests;

public sealed class FakeStudioViewStore : IStudioViewStore
{
    private readonly List<StudioView> _stored = [];

    public StudioView? LastSaved { get; private set; }

    public (string Store, string Tenant, string Owner, string Kind, string Key)? LastDelete { get; private set; }

    public int SaveCount { get; private set; }

    public void Seed(params StudioView[] views) => _stored.AddRange(views);

    public Task SaveAsync(StudioView view, CancellationToken ct = default)
    {
        LastSaved = view;
        SaveCount++;
        _stored.RemoveAll(v =>
            v.Store == view.Store && v.Tenant == view.Tenant && v.Owner == view.Owner &&
            v.Kind == view.Kind && v.Key == view.Key);
        _stored.Add(view);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StudioView>> ListAsync(
        string store, string tenant, string? kind = null, CancellationToken ct = default)
    {
        IReadOnlyList<StudioView> result =
        [
            .. _stored
                .Where(v => v.Store == store && v.Tenant == tenant && (kind is null || v.Kind == kind))
                .OrderByDescending(v => v.UpdatedAt)
        ];
        return Task.FromResult(result);
    }

    public Task<StudioView?> GetAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default) =>
        Task.FromResult(_stored.FirstOrDefault(v =>
            v.Store == store && v.Tenant == tenant && v.Owner == owner && v.Kind == kind && v.Key == key));

    public Task DeleteAsync(
        string store, string tenant, string owner, string kind, string key, CancellationToken ct = default)
    {
        LastDelete = (store, tenant, owner, kind, key);
        _stored.RemoveAll(v =>
            v.Store == store && v.Tenant == tenant && v.Owner == owner && v.Kind == kind && v.Key == key);
        return Task.CompletedTask;
    }
}
