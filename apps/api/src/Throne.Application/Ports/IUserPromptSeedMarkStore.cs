using Throne.Application.Manifest;

namespace Throne.Application.Ports;

/// <summary>Per-key «last seeded text» of the user-prompt seed (ADR-0051 amendment).</summary>
public interface IUserPromptSeedMarkStore
{
    Task<IReadOnlyList<UserPromptSeedMark>> ListAsync(CancellationToken ct);

    /// <summary>Insert-or-replace by key. Must run inside <see cref="IUnitOfWork.ExecuteAsync"/>.</summary>
    Task UpsertAsync(IReadOnlyList<UserPromptSeedMark> marks, CancellationToken ct);
}
