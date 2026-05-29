using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

public sealed class PackValidationEndpointTests
{
    [Test]
    public async Task PackValidationEndpoint_EnforcesByteCapForUnknownLengthBodies()
    {
        await using var fixture = new SidecarTestFixture();

        using var smallRequest = new HttpRequestMessage(HttpMethod.Post, "/api/packs/validate")
        {
            Content = new UnknownLengthStringContent(BuildPackJson(new string('a', 128))),
        };

        using HttpResponseMessage smallResponse = await fixture.Client.SendAsync(smallRequest);
        Assert.That(smallResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using JsonDocument smallBody = JsonDocument.Parse(await smallResponse.Content.ReadAsStringAsync());
        Assert.That(smallBody.RootElement.GetProperty("IsValid").GetBoolean(), Is.True);

        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/packs/validate")
        {
            Content = new UnknownLengthStringContent(BuildPackJson(new string('b', 1_000_128))),
        };

        using HttpResponseMessage oversizedResponse = await fixture.Client.SendAsync(oversizedRequest);
        Assert.That(oversizedResponse.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(oversizedResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));

        using JsonDocument oversizedBody = JsonDocument.Parse(await oversizedResponse.Content.ReadAsStringAsync());
        Assert.That(oversizedBody.RootElement.GetProperty("title").GetString(), Is.EqualTo("Payload Too Large"));
        Assert.That(oversizedBody.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(413));
        Assert.That(oversizedBody.RootElement.GetProperty("detail").GetString(),
            Is.EqualTo("Pack validation payloads must be 1000000 bytes or smaller."));
        Assert.That(oversizedBody.RootElement.GetProperty("instance").GetString(), Is.EqualTo("/api/packs/validate"));
        Assert.That(oversizedBody.RootElement.GetProperty("traceId").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task PackValidationEndpoint_WhenJsonIsMalformed_ReturnsStableParseLocationMessage()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/packs/validate")
        {
            Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement firstError = body.RootElement.GetProperty("Errors")[0];
        Assert.That(firstError.GetProperty("Path").GetString(), Is.EqualTo("$"));
        Assert.That(firstError.GetProperty("Message").GetString(),
            Does.Match(@"^Pack JSON could not be parsed near line \d+, byte \d+\.$"));
        Assert.That(firstError.GetProperty("Message").GetString(), Does.Not.Contain("invalid"));
    }

    private static string BuildPackJson(string backstory) =>
        $$"""
        {
          "Name": "Starter Pack",
          "Author": "QA",
          "Characters": [
            {
              "Id": "chill-1",
              "Name": "CampGuardian"
            }
          ],
          "MemorySeeds": [
            {
              "CharacterId": "chill-1",
              "Content": "{{backstory}}",
              "Importance": 0.6
            }
          ]
        }
        """;

    private sealed class UnknownLengthStringContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthStringContent(string content)
        {
            _bytes = Encoding.UTF8.GetBytes(content);
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
