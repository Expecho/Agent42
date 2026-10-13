using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// <c>POST /api/email-in</c> (ADR 0095), hosted in-memory against a real cluster: a Mailgun-shaped, locally signed POST
/// to an agent's address runs that agent in a new conversation of the agent's OWNER (never <c>local-dev</c>), and every
/// failure is refused with the status Mailgun's retry rule calls for.
/// </summary>
public sealed class EmailApiTests(EmailApiFixture fixture) : SharedClusterTests<EmailApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    // AC1: a signed message to a known address answers 200 and runs a turn in a new listed conversation of the owner.
    [Fact]
    public async Task EmailIn_ValidSignedMessage_RunsTurn()
    {
        var (user, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(Address(agent), subject: "Quarterly numbers", body: "Please summarise.");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var convId = await ConversationIdAsync(res);
        var turns = await PollMessagesAsync(user, convId, t => t.Any(x => x.Role == "assistant"));
        var userTurn = Assert.Single(turns, t => t.Role == "user");
        Assert.Contains("Subject: Quarterly numbers", userTurn.Text, StringComparison.Ordinal);
        Assert.Contains("Please summarise.", userTurn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(agent.EmailToken!, userTurn.Text, StringComparison.Ordinal);

        var listed = await GetAsync<List<ConversationInfo>>(user, "/api/conversations");
        var info = Assert.Single(listed, c => c.Id == convId);
        Assert.Equal("Quarterly numbers", info.Title);
        Assert.Equal(agent.Id, info.AgentId);
    }

    // AC2: a bad, absent or stale signature is a 401, and so is a replayed token.
    [Fact]
    public async Task EmailIn_BadSignature_Returns401()
    {
        var (_, agent) = await NewAgentAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var tampered = await PostEmailAsync(Address(agent), signature: new string('0', 64));
        using var unsigned = await PostEmailAsync(Address(agent), signature: "");
        using var wrongKey = await PostEmailAsync(Address(agent),
            signature: MailgunWebhook.Sign("another-key", now, "tok-wrong-key"), timestamp: now, token: "tok-wrong-key");
        using var stale = await PostEmailAsync(Address(agent),
            timestamp: DateTimeOffset.UtcNow.AddHours(-9).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    // D-S2: the HMAC does not cover the body, so a captured triple must not be reusable with another body.
    [Fact]
    public async Task EmailIn_ReplayedToken_Returns401()
    {
        var (_, agent) = await NewAgentAsync();
        var token = $"tok-{Guid.NewGuid():N}";

        using var first = await PostEmailAsync(Address(agent), body: "original", token: token);
        using var replay = await PostEmailAsync(Address(agent), body: "attacker text", token: token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    // AC3 + AC4: an unknown address and a deleted agent's address are refused alike, with 406 (never retried).
    [Fact]
    public async Task EmailIn_UnknownAddressAndDeletedAgent_Return406Alike()
    {
        var (user, agent) = await NewAgentAsync();
        (await Client.SendAsync(Request(HttpMethod.Delete, $"/api/agents/{agent.Id}", user))).EnsureSuccessStatusCode();

        using var unknown = await PostEmailAsync($"{EmailAddress.Generate().Token}@{EmailApiFixture.Domain}");
        using var deleted = await PostEmailAsync(Address(agent));

        Assert.Equal(HttpStatusCode.NotAcceptable, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotAcceptable, deleted.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await deleted.Content.ReadAsStringAsync());
    }

    // AC5: mail systems and people do not preserve case, so an uppercased local part still resolves.
    [Fact]
    public async Task EmailIn_UppercasedAddress_StillResolves()
    {
        var (_, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(Address(agent).ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // The recipient field may carry several addresses and a display name; the first that resolves wins.
    [Fact]
    public async Task EmailIn_RecipientList_FirstResolvingAddressWins()
    {
        var (_, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(
            $"someone@example.com, \"Agent\" <{agent.EmailToken}+invoices@{EmailApiFixture.Domain}>");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // AC6: an attached image rides onto the turn; a document is ingested for retrieval.
    [Fact]
    public async Task EmailIn_WithImageAndDocument_AttachesAndIngests()
    {
        var (user, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(Address(agent), files:
        [
            (new byte[] { 1, 2, 3, 4 }, "image/png", "pic.png"),
            (TestDocuments.SampleTwoPagePdf(), DocumentMediaTypes.Pdf, "guide.pdf"),
        ]);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var convId = await ConversationIdAsync(res);
        var turns = await PollMessagesAsync(user, convId, t => t.Any(x => x.Role == "assistant"));
        Assert.Contains(Assert.Single(turns, t => t.Role == "user").Attachments!, a => a.FileName == "pic.png");

        var query = (await new DeterministicEmbeddingGenerator().GenerateAsync(["magrathea"]))[0].Vector;
        var hits = await new InMemoryDocumentIndex().SearchAsync($"{user}/{convId}", query, topK: 3);
        Assert.Contains(hits, h => h.FileName == "guide.pdf");
    }

    // AC7 (diverged, ADR 0095): an unusable attachment is skipped and the agent told, never the whole email dropped —
    // the sender gets no reply, so a rejected email would vanish without trace.
    [Fact]
    public async Task EmailIn_UnsupportedFile_IsSkippedAndNoted()
    {
        var (user, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(Address(agent), files: [(new byte[] { 1, 2, 3 }, "application/octet-stream", "invite.ics.exe")]);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var turns = await PollMessagesAsync(user, await ConversationIdAsync(res), t => t.Any(x => x.Role == "assistant"));
        Assert.Contains("[Attachment skipped: 'invite.ics.exe'", Assert.Single(turns, t => t.Role == "user").Text,
            StringComparison.Ordinal);
    }

    // A signed message with nothing to read (no subject, no text body, no usable file) is refused with 406.
    [Fact]
    public async Task EmailIn_NothingToRead_Returns406()
    {
        var (_, agent) = await NewAgentAsync();

        using var res = await PostEmailAsync(Address(agent), subject: "", body: "", html: "<p>Only HTML</p>");

        Assert.Equal(HttpStatusCode.NotAcceptable, res.StatusCode);
    }

    // The signature is inside the body, so an over-cap request is refused on its Content-Length before the form is read.
    [Fact]
    public async Task EmailIn_OverSizeCap_IsRefusedBeforeTheFormIsRead()
    {
        using var content = new ByteArrayContent(new byte[EmailLimits.MaxRequestBytes + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data") { Parameters = { new("boundary", "x") } };

        using var res = await Client.PostAsync("/api/email-in", content);

        Assert.Equal(HttpStatusCode.NotAcceptable, res.StatusCode);
    }

    // AC12: the email token is a different credential namespace — it never authenticates /api/agent-run.
    [Fact]
    public async Task EmailToken_DoesNotAuthenticateAgentRun()
    {
        var (_, agent) = await NewAgentAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent-run")
        {
            Content = new MultipartFormDataContent { { new StringContent("hi"), "prompt" } },
        };
        request.Headers.Add("X-Api-Key", agent.EmailToken);
        using var res = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // AC10 at the API: regenerateEmail on PUT rotates the address; the old one stops resolving.
    [Fact]
    public async Task Put_RegenerateEmail_RotatesTheAddress()
    {
        var (user, agent) = await NewAgentAsync();

        var response = await Client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{agent.Id}", user,
            new AgentUpsertRequest(agent.Name, "", agent.Model, agent.SystemPrompt, RegenerateEmail: true)));
        response.EnsureSuccessStatusCode();
        var rotated = (await response.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        Assert.NotEqual(agent.EmailToken, rotated.EmailToken);
        using var old = await PostEmailAsync(Address(agent));
        using var fresh = await PostEmailAsync(Address(rotated));
        Assert.Equal(HttpStatusCode.NotAcceptable, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task EmailConfig_ReportsEnabledAndDomain_NeverTheKey()
    {
        var raw = await Client.GetStringAsync("/api/email/config");

        using var json = JsonDocument.Parse(raw);
        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(EmailApiFixture.Domain, json.RootElement.GetProperty("domain").GetString());
        Assert.DoesNotContain(EmailApiFixture.SigningKey, raw, StringComparison.Ordinal);
    }

    private static string Address(AgentDefinition agent) => $"{agent.EmailToken}@{EmailApiFixture.Domain}";

    private async Task<(string User, AgentDefinition Agent)> NewAgentAsync()
    {
        var user = $"email-api-{Guid.NewGuid():N}";
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api/agents", user,
            new AgentUpsertRequest("Mailbot", "", "gpt-4o", "You read mail.")));
        response.EnsureSuccessStatusCode();
        return (user, (await response.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent);
    }

    // POSTs a Mailgun-shaped multipart form (ADR 0095 spike findings), signed with the fixture's key unless the test
    // overrides a field. No principal header: the endpoint is on excludedPaths and must not need one.
    private async Task<HttpResponseMessage> PostEmailAsync(
        string recipient, string subject = "Hello", string body = "Body text.", string? html = null,
        string? timestamp = null, string? token = null, string? signature = null,
        (byte[] Bytes, string ContentType, string FileName)[]? files = null)
    {
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        token ??= $"tok-{Guid.NewGuid():N}";
        signature ??= MailgunWebhook.Sign(EmailApiFixture.SigningKey, timestamp, token);

        var form = new MultipartFormDataContent
        {
            { new StringContent(timestamp), "timestamp" },
            { new StringContent(token), "token" },
            { new StringContent(signature), "signature" },
            { new StringContent(recipient), "recipient" },
            { new StringContent("Arthur Dent <arthur@example.com>"), "from" },
            { new StringContent(subject), "subject" },
            { new StringContent(body), "body-plain" },
        };
        if (html is not null)
        {
            form.Add(new StringContent(html), "body-html");
        }

        var index = 0;
        foreach (var (bytes, contentType, fileName) in files ?? [])
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, $"attachment-{++index}", fileName);
        }

        if (index > 0)
        {
            form.Add(new StringContent(index.ToString(CultureInfo.InvariantCulture)), "attachment-count");
        }

        return await Client.PostAsync("/api/email-in", form);
    }

    private static async Task<string> ConversationIdAsync(HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString()!;

    private async Task<T> GetAsync<T>(string user, string uri)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, uri, user));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    // The turn runs in the background (OneWay), so the reply is not present the instant 200 returns.
    private async Task<List<ChatTurn>> PollMessagesAsync(string user, string convId, Func<List<ChatTurn>, bool> until)
    {
        List<ChatTurn> turns = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            turns = await GetAsync<List<ChatTurn>>(user, $"/api/conversations/{convId}/messages");
            if (until(turns))
            {
                return turns;
            }

            await Task.Delay(100);
        }

        return turns;
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}

/// <summary>AC8 (ADR 0095): with no signing key the endpoint answers 503 and accepts nothing, even a well-formed POST.</summary>
public sealed class EmailApiNoKeyTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    [Fact]
    public async Task EmailIn_NoSigningKey_Returns503()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(timestamp), "timestamp" },
            { new StringContent("tok"), "token" },
            { new StringContent(MailgunWebhook.Sign("", timestamp, "tok")), "signature" },
            { new StringContent("agent-x@in.test.example"), "recipient" },
            { new StringContent("hi"), "body-plain" },
        };

        using var res = await Fixture.Client.PostAsync("/api/email-in", form);
        var config = await Fixture.Client.GetFromJsonAsync<JsonElement>("/api/email/config");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.False(config.GetProperty("enabled").GetBoolean());
    }
}
