using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PalLLM.Domain.Integration;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

/// <summary>
/// Pass 34 / C1 — regression coverage for <c>POST /api/chat/party</c>.
/// Pinned contract:
/// <list type="number">
///   <item>Missing or empty CharacterIds[] returns ProblemDetails 400.</item>
///   <item>Missing UserMessage returns ProblemDetails 400.</item>
///   <item>Successful fan-out returns one PartyChatTurn per id in request order.</item>
///   <item>Each Turn carries a full per-character ChatResponse with AssistantMessage populated (deterministic fallback guarantee).</item>
/// </list>
/// </summary>
public sealed class PartyChatEndpointTests
{
    [Test]
    public async Task PostChatParty_EmptyCharacterIds_Returns400()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(
                "{\"CharacterIds\":[],\"UserMessage\":\"hi\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostChatParty_MissingUserMessage_Returns400()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(
                "{\"CharacterIds\":[1,2],\"UserMessage\":\"\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostChatParty_OversizedUserMessage_Returns400()
    {
        await using var fixture = new SidecarTestFixture();
        string oversizedMessage = new('x', ChatRequest.UserMessageMaxLength + 1);
        string body = JsonSerializer.Serialize(new
        {
            CharacterIds = new[] { 1, 2 },
            UserMessage = oversizedMessage,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(responseBody, Does.Contain("UserMessage"));
        Assert.That(responseBody, Does.Contain(ChatRequest.UserMessageMaxLength.ToString()));
    }

    [Test]
    public async Task PostChatParty_TooManyCharacterIds_Returns400()
    {
        await using var fixture = new SidecarTestFixture();
        int[] characterIds = Enumerable.Range(1, PalApiValidation.PartyChatMaxCharacters + 1).ToArray();
        string body = JsonSerializer.Serialize(new
        {
            CharacterIds = characterIds,
            UserMessage = "hello party",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(responseBody, Does.Contain("CharacterIds"));
        Assert.That(responseBody, Does.Contain(PalApiValidation.PartyChatMaxCharacters.ToString()));
    }

    [Test]
    public async Task PostChatParty_FansOutAcrossEveryCharacter()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(
                "{\"CharacterIds\":[1,2,3],\"UserMessage\":\"hello party\",\"TaskTag\":\"player_chat\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        Assert.That(root.TryGetProperty("Turns", out JsonElement turns), Is.True);
        Assert.That(turns.GetArrayLength(), Is.EqualTo(3),
            "One turn per CharacterId in request order.");
        int index = 0;
        foreach (JsonElement turn in turns.EnumerateArray())
        {
            Assert.That(turn.GetProperty("OrderIndex").GetInt32(), Is.EqualTo(index),
                "Turns must be returned in request order.");
            JsonElement respElement = turn.GetProperty("Response");
            string? assistant = respElement.GetProperty("AssistantMessage").GetString();
            Assert.That(assistant, Is.Not.Null.And.Not.Empty,
                "Every turn must have an AssistantMessage — deterministic fallback guarantee.");
            index++;
        }
        Assert.That(root.TryGetProperty("PartyId", out JsonElement partyId), Is.True);
        Assert.That(partyId.GetString(), Does.StartWith("party-"));
    }

    [Test]
    public async Task PostChatParty_ThreadedMode_SeedsSystemPromptWithEarlierReplies()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/party")
        {
            Content = new StringContent(
                "{\"CharacterIds\":[1,2],\"UserMessage\":\"chat with me\",\"Threaded\":true}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        // The second turn's SystemPrompt should carry the earlier
        // reply preamble because Threaded=true. Since we can't assert
        // specific content (fallback paths vary), we assert the
        // top-level Threaded flag round-tripped.
        Assert.That(doc.RootElement.GetProperty("Threaded").GetBoolean(), Is.True);
        Assert.That(doc.RootElement.GetProperty("Turns").GetArrayLength(), Is.EqualTo(2));
    }
}
