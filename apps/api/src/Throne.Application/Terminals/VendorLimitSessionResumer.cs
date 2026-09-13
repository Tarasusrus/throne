using Throne.Application.Git;
using Throne.Application.Ports;

namespace Throne.Application.Terminals;

/// <summary>
/// The two hands of the vendor-limit sweep (ADR-0055). A nudge types the continuation prompt into
/// the live pane and submits it: if the limit is still on, the vendor answers with another
/// <c>StopFailure rate_limit</c> and the hook handler counts a repeat; if it is off, the agent
/// resumes and <c>PostToolUse</c> closes the pause. A relaunch re-runs the normal spawn pipeline
/// with the persisted launch axis, the adapter's continue switch and the rules block the previous
/// spawn left in the workspace, then delivers the same continuation prompt.
/// </summary>
public sealed class VendorLimitSessionResumer(
    ITmuxSessionManager tmux,
    IIntentTerminalLaunchStore launches,
    IWorkspaceRootProvider workspaceRoot,
    IEnumerable<ISessionHookAdapter> hookAdapters,
    RunPreflightOrchestrator preflight) : IVendorLimitSessionResumer
{
    public const string ContinuationPrompt =
        "Лимит вендора снят. Продолжай задачу с того места, где остановился: сначала сверься с git status "
        + "и своим промежуточным отчётом в теле интента, потом работай дальше.";

    private readonly Dictionary<string, ISessionHookAdapter> _hookAdapters =
        hookAdapters.ToDictionary(a => a.Vendor, StringComparer.Ordinal);

    public async Task NudgeAsync(VendorLimitPause pause, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pause);
        await tmux.SendLiteralTextAsync(pause.IntentId, ContinuationPrompt, ct);
        await tmux.SendEnterAsync(pause.IntentId, ct);
    }

    public async Task RelaunchAsync(VendorLimitPause pause, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pause);
        var launch = await launches.GetAsync(pause.IntentId, ct)
            ?? throw new InvalidOperationException(
                $"Intent '{pause.IntentId}' has no persisted launch record; cannot relaunch after the vendor limit.");

        var workspacePath = Path.Combine(workspaceRoot.ResolvedRoot, "intents", pause.IntentId);
        string? systemPrompt = null;
        if (_hookAdapters.TryGetValue(launch.Vendor, out var adapter))
        {
            systemPrompt = await adapter.ReadPersistedSystemPromptAsync(workspacePath, ct);
        }

        await preflight.RunAsync(
            pause.IntentId,
            launch.Mode,
            new TerminalLaunchInput(launch.Vendor, launch.Model, launch.Effort, ResumeConversation: true),
            new TerminalSpawnPrompt(systemPrompt, ContinuationPrompt, SelectedPartIds: null, IntentTextSave: null),
            ct);
    }
}
