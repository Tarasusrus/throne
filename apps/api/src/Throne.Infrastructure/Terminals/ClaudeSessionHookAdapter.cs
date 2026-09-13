using System.Text.Encodings.Web;
using System.Text.Json;
using Throne.Application.Terminals;

namespace Throne.Infrastructure.Terminals;

/// <summary>
/// Claude flavour of <see cref="ISessionHookAdapter"/>: writes a per-session settings file in the
/// intent workspace root and points the CLI at it with <c>--settings &lt;file&gt;</c>. The assembled
/// rules block is written beside it and referenced with <c>--append-system-prompt-file</c> (a
/// native Claude flag) rather than inlined — a multi-KB <c>--append-system-prompt</c> token would
/// blow past tmux's spawn-argv imsg limit. Both files live beside the clone (never inside it), so
/// the repo stays free of runtime state and the workspace teardown on intent-done reaps them.
/// </summary>
public sealed class ClaudeSessionHookAdapter(
    SessionHookOptions options,
    ISessionSkillMaterializer skillMaterializer) : ISessionHookAdapter
{
    private const string SettingsFileName = "throne-session.settings.json";
    private const string SystemPromptFileName = "throne-session.append-system-prompt.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public string Vendor => TerminalAgentCatalog.VendorClaude;

    // `--continue` resumes the most recent conversation of the cwd (the intent workspace root, which
    // is the tmux session cwd), so a relaunch after a vendor-limit pause keeps the agent's context.
    public IReadOnlyList<string> ResumeArgs => ["--continue"];

    public async Task<string?> ReadPersistedSystemPromptAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var path = Path.Combine(workspacePath, SystemPromptFileName);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public async Task<IReadOnlyList<string>> PrepareSpawnArgsAsync(
        string intentId,
        string workspacePath,
        string mode,
        string? systemPrompt,
        IReadOnlyList<SessionSkillPackage> skillPackages,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        Directory.CreateDirectory(workspacePath);
        await skillMaterializer.MaterializeAsync(
            workspacePath, TerminalAgentCatalog.VendorClaude, skillPackages, ct);

        var settingsPath = Path.Combine(workspacePath, SettingsFileName);
        await using (var stream = File.Create(settingsPath))
        {
            await JsonSerializer.SerializeAsync(stream, BuildSettings(intentId, mode), JsonOptions, ct);
            await stream.WriteAsync("\n"u8.ToArray(), ct);
        }

        var args = new List<string> { "--settings", settingsPath };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            var systemPromptPath = Path.Combine(workspacePath, SystemPromptFileName);
            await File.WriteAllTextAsync(systemPromptPath, systemPrompt!, ct);
            args.Add("--append-system-prompt-file");
            args.Add(systemPromptPath);
        }

        return args;
    }

    // Claude's per-session files (settings + system prompt) live inside the intent workspace, so the
    // workspace-folder removal on intent-done reaps them — no out-of-workspace state to clean here.
    public Task CleanupAsync(string intentId, CancellationToken ct) => Task.CompletedTask;

    // Claude Code v2.1.x renders the ready composer as a horizontal-rule frame with a `❯` prompt
    // glyph on the input row (the older `│ >` box-drawing frame is gone). The boot splash never
    // paints `❯`, so this matches only when the composer is up and listening for paste.
    public bool IsTuiReady(string paneSnapshot) =>
        !string.IsNullOrEmpty(paneSnapshot)
        && paneSnapshot.Contains('❯');

    // After Enter, Claude Code prints a working footer while the model is processing the prompt.
    // Very short prompts can finish between captures; the completed-response footer is the same
    // positive signal, and avoids a stale "prompt not sent" warning after the agent already replied.
    public bool IsPromptSubmitted(string paneSnapshot) =>
        !string.IsNullOrEmpty(paneSnapshot)
        && (paneSnapshot.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase)
            || paneSnapshot.Contains("Brewed for", StringComparison.OrdinalIgnoreCase));

    // autoContinueAtUsageLimit: Claude Code's own «wait for the usage limit to reset and continue»
    // (default on, but remotely flag-gated and toggleable by the operator's user settings) — set
    // explicitly so a Throne session never depends on the operator's global choice (ADR-0055).
    // Several bindings may target one hook event (Notification: permission_prompt and the
    // quota_auto_resume_* family); Claude takes them as separate matcher groups under that event.
    private object BuildSettings(string intentId, string mode) =>
        new
        {
            autoContinueAtUsageLimit = true,
            hooks = TerminalHookEvents.ClaudeBindings
                .GroupBy(binding => binding.Event, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(binding => BuildHookGroup(binding, intentId, mode)).ToArray(),
                    StringComparer.Ordinal),
        };

    // A Claude hook group is `{ hooks: [...] }`, optionally prefixed with a `matcher` that scopes
    // which instances of the event fire it (e.g. only `permission_prompt` Notifications). The matcher
    // key is omitted entirely when absent so unscoped events keep matching every occurrence.
    private object BuildHookGroup(TerminalHookBinding binding, string intentId, string mode)
    {
        var hooks = new[]
        {
            new
            {
                type = "command",
                command = TerminalHookCallback.CurlCommand(
                    options.ApiBaseUrl, intentId, binding.Event, mode),
                timeout = 10,
            },
        };

        if (binding.Matcher is null)
        {
            return new { hooks };
        }

        return new { matcher = binding.Matcher, hooks };
    }
}
