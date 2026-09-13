using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Throne.Api.Terminals;
using Throne.Application.Terminals;

namespace Throne.Api.Tests.Terminals;

/// <summary>
/// Тело хука читается терпимо: любая форма, пустота, мусор — не ошибка, а пустой payload.
/// Терять статусный переход из-за тела нельзя (ADR-0055).
/// </summary>
public class TerminalHookPayloadReaderTests
{
    [Fact(DisplayName = "Реальное тело StopFailure Claude: error и last_assistant_message прочитаны, остальное отброшено")]
    public async Task Reads_known_fields_from_claude_body()
    {
        var body = """
            {"session_id":"s","transcript_path":"/x.jsonl","cwd":"/w","permission_mode":"default",
             "hook_event_name":"StopFailure","error":"rate_limit",
             "last_assistant_message":"You've hit your monthly spend limit · your session limit resets 1:40pm (Asia/Bangkok)"}
            """;

        var payload = await TerminalHookPayloadReader.ReadAsync(Request(body), CancellationToken.None);

        payload.Should().Be(new TerminalHookPayload(
            "rate_limit",
            "You've hit your monthly spend limit · your session limit resets 1:40pm (Asia/Bangkok)",
            null,
            null));
    }

    [Fact(DisplayName = "Notification Claude: notification_type и message")]
    public async Task Reads_notification_fields()
    {
        var body = """{"hook_event_name":"Notification","notification_type":"quota_auto_resume_fired","message":"Usage limit available — Claude is continuing your task"}""";

        var payload = await TerminalHookPayloadReader.ReadAsync(Request(body), CancellationToken.None);

        payload.NotificationType.Should().Be("quota_auto_resume_fired");
        payload.Message.Should().Be("Usage limit available — Claude is continuing your task");
    }

    [Theory(DisplayName = "Пустое, битое, не-объектное тело — Empty, без исключений")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"string\"")]
    [InlineData("{\"error\": {\"nested\": true}, \"message\": 42}")]
    [InlineData("{\"error\": \"rate_limit\", ")]
    public async Task Garbage_collapses_to_empty(string body)
    {
        var payload = await TerminalHookPayloadReader.ReadAsync(Request(body), CancellationToken.None);

        payload.Should().Be(TerminalHookPayload.Empty);
    }

    [Fact(DisplayName = "Свойство: для любого JSON-объекта читаются ровно четыре верхнеуровневых строковых поля")]
    public void Any_object_yields_only_top_level_strings()
    {
        string[] keys = ["error", "last_assistant_message", "notification_type", "message", "cwd", "session_id"];
        for (var seed = 0; seed < 200; seed++)
        {
            var rng = new Random(seed);
            var obj = new Dictionary<string, object?>();
            var expected = new Dictionary<string, string?>();
            foreach (var key in keys)
            {
                switch (rng.Next(4))
                {
                    case 0: break;
                    case 1: obj[key] = "v" + rng.Next(100); expected[key] = (string)obj[key]!; break;
                    case 2: obj[key] = rng.Next(100); break;
                    default: obj[key] = new { nested = true }; break;
                }
            }
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(obj));

            var payload = TerminalHookPayloadReader.Parse(doc.RootElement);

            var why = $"seed {seed}: {JsonSerializer.Serialize(obj)}";
            payload.Error.Should().Be(expected.GetValueOrDefault("error"), why);
            payload.LastAssistantMessage.Should().Be(expected.GetValueOrDefault("last_assistant_message"), why);
            payload.NotificationType.Should().Be(expected.GetValueOrDefault("notification_type"), why);
            payload.Message.Should().Be(expected.GetValueOrDefault("message"), why);
        }
    }

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context.Request;
    }
}
