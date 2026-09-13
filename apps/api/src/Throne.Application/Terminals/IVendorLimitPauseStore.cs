using System.Collections.Concurrent;

namespace Throne.Application.Terminals;

/// <summary>
/// Per-intent vendor-limit pauses (ADR-0055). Process-local by design: the vendor session lives
/// in tmux and waits on its own, so a Throne restart loses only the «paused until» display and
/// the attempt counter — the next limit hit or tool call rebuilds them from hooks.
/// </summary>
public interface IVendorLimitPauseStore
{
    VendorLimitPause? Find(string intentId);

    void Put(VendorLimitPause pause);

    bool Remove(string intentId);

    IReadOnlyList<VendorLimitPause> All();
}

public sealed class InMemoryVendorLimitPauseStore : IVendorLimitPauseStore
{
    private readonly ConcurrentDictionary<string, VendorLimitPause> _pauses = new(StringComparer.Ordinal);

    public VendorLimitPause? Find(string intentId) =>
        _pauses.TryGetValue(intentId, out var pause) ? pause : null;

    public void Put(VendorLimitPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        _pauses[pause.IntentId] = pause;
    }

    public bool Remove(string intentId) => _pauses.TryRemove(intentId, out _);

    public IReadOnlyList<VendorLimitPause> All() => _pauses.Values.ToArray();
}
