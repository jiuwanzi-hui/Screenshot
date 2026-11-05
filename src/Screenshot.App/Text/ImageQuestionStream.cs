using System.IO;
using System.Text;
using System.Text.Json;

namespace Screenshot.App.Text;

internal sealed record ImageQuestionProgress(string Answer, string Reasoning);

// Parses bounded SSE without buffering the entire answer before displaying it.
// Only provider-returned text is shown; reasoning is never added to chat history.
internal static class ImageQuestionStream
{
    internal static async Task<ImageQuestionReply> ReadAsync(Stream stream, bool streaming,
        Action<ImageQuestionProgress>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        var pending = new StringBuilder();
        var data = new StringBuilder();
        var buffer = new char[4096];
        var total = 0;
        var completed = false;
        var done = false;
        string? finish = null;
        var lastReport = 0L;
        void Report(bool force = false)
        {
            if (progress is null || !force && Environment.TickCount64 - lastReport < 75) return;
            lastReport = Environment.TickCount64;
            progress(new(answer.ToString(), reasoning.ToString()));
        }
        void ReadMessage(JsonElement message)
        {
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                answer.Append(content.GetString());
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
                answer.Append(refusal.GetString());
            // Compatible providers expose their own public reasoning/summary field.
            foreach (var key in new[] { "reasoning_content", "reasoning" })
                if (message.TryGetProperty(key, out var thought) && thought.ValueKind == JsonValueKind.String)
                { reasoning.Append(thought.GetString()); break; }
        }
        void Event(string json, bool full = false)
        {
            if (json.Trim() == "[DONE]") { completed = true; done = true; return; }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _)) throw new InvalidDataException("模型在生成过程中返回错误，已保留收到的内容，请重试。");
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.TryGetProperty(full ? "message" : "delta", out var message)) ReadMessage(message);
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            { finish = reason.GetString(); completed = true; }
            if (full) completed = true;
            Report();
        }
        void Line(string line)
        {
            line = line.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (data.Length > 0) { Event(data.ToString()); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
        try
        {
            int count;
            while (!done && (count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > 2 * 1024 * 1024) throw new InvalidDataException("模型响应过大，请缩短请求。");
                if (!streaming) { pending.Append(buffer, 0, count); continue; }
                for (var i = 0; i < count && !done; i++)
                {
                    if (buffer[i] == '\n') { Line(pending.ToString()); pending.Clear(); }
                    else pending.Append(buffer[i]);
                }
            }
            if (streaming)
            {
                if (pending.Length > 0) Line(pending.ToString());
                Line("");
            }
            else Event(pending.ToString(), full: true);
            Report(force: true);
            if (!completed) return new(false, answer.ToString(), "模型连接提前结束，回答未完成；已保留收到的内容。");
            if (answer.Length == 0) return new(false, "", "模型没有返回最终回答，请重试或更换模型。");
            return new(true, answer.ToString(), finish == "length" ? "回答达到模型长度限制，可继续追问。" : null);
        }
        catch (OperationCanceledException) { Report(force: true); throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            Report(force: true);
            return new(false, answer.ToString(), e is InvalidDataException ? e.Message : "模型响应中断或格式不兼容，已保留收到的内容。");
        }
    }
}
