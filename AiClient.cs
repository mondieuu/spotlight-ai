using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Odak;

public sealed record ChatTurn(bool IsUser, string Text);

/// <summary>A problem the user can act on; its message is shown in the panel.</summary>
public sealed class AiException : Exception
{
    public AiException(string message) : base(message) { }
}

/// <summary>Streams answers from OpenRouter with the user's own key. Nothing else is contacted.</summary>
public static class AiClient
{
    public const string DefaultModel = "openrouter/auto";
    public const string KeyPage = "https://openrouter.ai/settings/keys";
    internal static string BaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>Finds the key in what was pasted, which may include spaces or other text around it. Null if there is none.</summary>
    public static string? FindKey(string? pasted)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pasted ?? "", @"sk-or-[A-Za-z0-9_-]{20,}");
        return match.Success ? match.Value : null;
    }

    private const string Instructions = "Answer briefly and directly. Write plain text without Markdown formatting. Reply in the language of the question.";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async IAsyncEnumerable<string> StreamAsync(string key, string model, IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content = Instructions } };
        foreach (var turn in history) messages.Add(new { role = turn.IsUser ? "user" : "assistant", content = turn.Text });
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, messages, stream = true }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Title", "Spotlight AI");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw Failure((int)response.StatusCode, body);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // OpenRouter also sends ": OPENROUTER PROCESSING" comment lines to keep the connection open.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            // An error can also arrive in the middle of a stream.
            if (root.TryGetProperty("error", out var error))
                throw new AiException(MessageOf(error) ?? L.T("The service reported an error.", "Hizmet bir hata bildirdi."));
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                yield return content.GetString() ?? "";
        }
    }

    private static AiException Failure(int status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)) message = MessageOf(error);
        }
        catch (JsonException) { }
        return status switch
        {
            401 => new(L.T("The API key was not accepted. Check it in Settings.", "API anahtarı kabul edilmedi. Ayarlar'dan kontrol et.")),
            402 => new(L.T("Your OpenRouter credits have run out, or this model is not free.", "OpenRouter kredin bitmiş ya da bu model ücretsiz değil.")),
            429 => new(L.T("Usage limit reached. Try again later or pick another model.", "Kullanım limitine ulaşıldı. Sonra yeniden dene ya da başka model seç.")),
            _ => new(message ?? L.T($"The service answered with error {status}.", $"Hizmet {status} hatası verdi.")),
        };
    }

    private static string? MessageOf(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : null;
}
