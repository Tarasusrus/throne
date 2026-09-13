using System.Text.Json;
using Throne.Application.Terminals;

namespace Throne.Api.Terminals;

/// <summary>
/// Reads the vendor hook body into <see cref="TerminalHookPayload"/> without ever failing the
/// request: the body is whatever the vendor put on the hook's stdin (Claude / Codex JSON objects,
/// nothing at all from OpenCode's plugin shim), so absent, empty, non-JSON or non-object bodies
/// all collapse to <see cref="TerminalHookPayload.Empty"/>. Only top-level string fields are read.
/// </summary>
internal static class TerminalHookPayloadReader
{
    // A hook body is a few KB of JSON; anything larger is not a payload we know how to read.
    private const int MaxBodyBytes = 256 * 1024;

    public static async Task<TerminalHookPayload> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength is 0 or > MaxBodyBytes)
        {
            return TerminalHookPayload.Empty;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return TerminalHookPayload.Empty;
        }
        catch (InvalidOperationException)
        {
            return TerminalHookPayload.Empty;
        }
    }

    public static TerminalHookPayload Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return TerminalHookPayload.Empty;
        }

        return new TerminalHookPayload(
            Error: ReadString(root, "error"),
            LastAssistantMessage: ReadString(root, "last_assistant_message"),
            NotificationType: ReadString(root, "notification_type"),
            Message: ReadString(root, "message"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
