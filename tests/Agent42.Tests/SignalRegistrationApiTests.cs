using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> with Signal registered for real over a scripted daemon (<see cref="SignalDaemonStub"/>):
/// the real <see cref="SignalRpcClient"/> and <see cref="SignalAdapter"/>, a recording gateway control.
/// </summary>
public sealed class SignalRegistrationApiFixture : SharedApiFixture
{
    public SignalDaemonStub Daemon { get; } = new();

    public RecordingChatGatewayControl Control { get; } = new();

    protected override void Configure(WebApplicationBuilder builder)
    {
        var rpc = Daemon.Client();
        builder.Services.AddSingleton<IChatGatewaySecretProtector>(new ChatGatewaySecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<ChatGatewaySecretProtector>.Instance));
        builder.Services.AddSingleton(rpc);
        builder.Services.AddSingleton<IChatPlatformAdapter>(
            new SignalAdapter(rpc, TimeProvider.System, NullLogger<SignalAdapter>.Instance));
        builder.Services.AddSingleton<IChatGatewayControl>(Control);
    }
}

/// <summary>
/// Registering a Signal number (ADR 0111 phase 3; AC28, AC28b, AC30): the number is claimed before <c>register</c>, a
/// number another connection holds is refused without touching the daemon, a failed registration releases its claim,
/// verify turns the pending number into the credential, a Signal credential can never be pasted, and a delete makes the
/// daemon forget the number and frees it.
/// </summary>
public sealed class SignalRegistrationApiTests(SignalRegistrationApiFixture fixture) : SharedClusterTests<SignalRegistrationApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private SignalDaemonStub Daemon => Fixture.Daemon;

    [Fact]
    public async Task Register_ThenVerify_StoresTheNumber_AndOpensPairing()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var number = NewNumber();

        var registered = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number = Spaced(number) });

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var pending = await registered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ChatConnectionStatus.AwaitingCode, pending.GetProperty("status").GetString());
        Assert.Equal(number, pending.GetProperty("pendingSignalNumber").GetString());
        Assert.Contains(Daemon.Calls, c => c.Method == "register" && c.Params.GetProperty("account").GetString() == number);

        Daemon.Accounts[number] = "aaaaaaaa-0000-4000-8000-" + number[^12..];
        var verified = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/verify", user, new { code = "123-456" });

        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var view = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ChatConnectionStatus.AwaitingPairing, view.GetProperty("status").GetString());
        Assert.True(view.GetProperty("hasCredentials").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("pendingSignalNumber").ValueKind);
        Assert.Contains(Daemon.Calls, c => c.Method == "verify" && c.Params.GetProperty("verificationCode").GetString() == "123456");
        Assert.Equal(SignalNumberClaimState.Active, (await Claim(number).GetAsync())!.State);
        Assert.Contains($"restart:{id}", Fixture.Control.Calls);
    }

    // AC28: the claim is taken BEFORE register, so another user's number never reaches the daemon.
    [Fact]
    public async Task Register_ANumberAnotherConnectionHolds_Is409_AndNeverCallsRegister()
    {
        var number = NewNumber();
        await Claim(number).TryClaimAsync("someone-else", "their-connection", DateTimeOffset.UtcNow);
        var (user, id) = await NewSignalConnectionAsync();

        var response = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("someone-else", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Daemon.Calls, c => c.Method == "register" && c.Params.GetProperty("account").GetString() == number);
    }

    // AC28: a captcha demand is a 400 with the link, and the failed registration does not squat the number.
    [Fact]
    public async Task Register_WhenSignalWantsACaptcha_Is400WithTheLink_AndReleasesTheClaim()
    {
        var number = NewNumber();
        var (user, id) = await NewSignalConnectionAsync();
        Daemon.Errors["register"] = (-1, "Captcha required for verification, use --captcha CAPTCHA");
        try
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(AgentApi.SignalCaptchaUrl, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Null(await Claim(number).GetAsync());
        }
        finally
        {
            Daemon.Errors.TryRemove("register", out _);
        }
    }

    [Fact]
    public async Task Register_WithoutTheLease_Is503()
    {
        var (user, id) = await NewSignalConnectionAsync();
        Fixture.Control.HoldsLease = false;
        try
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number = NewNumber() });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            Fixture.Control.HoldsLease = true;
        }
    }

    [Theory]
    [InlineData("0612345678")]
    [InlineData("+0612345678")]
    [InlineData("+31 6")]
    [InlineData("call me")]
    public void ANumberThatIsNotE164_IsRefused(string typed) => Assert.Null(AgentApi.NormalizeSignalNumber(typed));

    [Theory]
    [InlineData("+31 6 1234 5678", "+31612345678")]
    [InlineData("0031-6-12345678", "+31612345678")]
    [InlineData("+1 (555) 010.9999", "+15550109999")]
    public void ATypedNumber_IsNormalised(string typed, string expected) => Assert.Equal(expected, AgentApi.NormalizeSignalNumber(typed));

    [Fact]
    public async Task Verify_WithoutARegistration_Is400()
    {
        var (user, id) = await NewSignalConnectionAsync();

        var response = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/verify", user, new { code = "123456" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC28b (B1): a Signal credential can never be pasted, on create or edit, so nobody can take over a number another
    // user registered by typing it in.
    [Fact]
    public async Task PastedSignalCredentials_AreRefused_OnCreateAndEdit()
    {
        // The number IS registered in the daemon (another user's): validation alone would accept it.
        Daemon.Accounts["+31600000001"] = "aaaaaaaa-0000-4000-8000-000000000099";
        var user = NewUser();
        var created = await SendAsync(HttpMethod.Post, "/api/chat-connections", user,
            new { platform = "Signal", name = "Hijack", credentials = new { botToken = "+31600000001" } });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var (owner, id) = await NewSignalConnectionAsync();
        var edited = await SendAsync(HttpMethod.Put, $"/api/chat-connections/{id}", owner,
            new { name = "Hijack", credentials = new { botToken = "+31600000001" } });
        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);

        var list = await (await SendAsync(HttpMethod.Get, "/api/chat-connections", owner)).Content.ReadFromJsonAsync<JsonElement>();
        var only = Assert.Single(list.GetProperty("connections").EnumerateArray());
        Assert.False(only.GetProperty("hasCredentials").GetBoolean());
        Assert.Equal(ChatConnectionStatus.NeedsRegistration, only.GetProperty("status").GetString());
    }

    // AC30: a verified connection's delete unregisters the number, deletes its keys and frees it, in that order after
    // the session stopped.
    [Fact]
    public async Task Delete_AVerifiedConnection_ForgetsTheNumber_AndReleasesIt()
    {
        var (user, id, number) = await NewVerifiedConnectionAsync();

        var deleted = await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        AssertForgotten(number);
        Assert.Contains($"stop:{id}", Fixture.Control.Calls);
        Assert.Null(await Claim(number).GetAsync());
    }

    // AC30 (M2): an abandoned registration (no credentials, a pending number) is forgotten and freed as well.
    [Fact]
    public async Task Delete_AnAbandonedRegistration_ForgetsAndReleasesThePendingNumber()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var number = NewNumber();
        await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });

        await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user);

        AssertForgotten(number);
        Assert.Null(await Claim(number).GetAsync());
    }

    // AC30: a daemon that fails the cleanup never undoes the delete or keeps the number claimed.
    [Fact]
    public async Task Delete_WhenTheDaemonFailsTheCleanup_StillDeletesAndReleases()
    {
        var (user, id, number) = await NewVerifiedConnectionAsync();
        Daemon.Errors["unregister"] = (-3, "network down");
        try
        {
            var deleted = await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user);

            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Null(await Claim(number).GetAsync());
            Assert.Contains(Daemon.Calls, c => c.Method == "deleteLocalAccountData" && c.Params.GetProperty("account").GetString() == number);
        }
        finally
        {
            Daemon.Errors.TryRemove("unregister", out _);
        }
    }

    // Critic M1: an abandoned registration's claim expired and another connection took the number. Deleting the old
    // connection must NOT make the daemon forget it: that would unregister the new holder's working number.
    [Fact]
    public async Task Delete_AStaleRegistration_WhoseNumberWasTakenOver_LeavesTheNewHolderAlone()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var number = NewNumber();
        await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });
        Assert.True(await Claim(number).TryClaimAsync("someone-else", "their-connection",
            DateTimeOffset.UtcNow + ChatGatewayLimits.SignalPendingClaimLifetime));

        await SendAsync(HttpMethod.Delete, $"/api/chat-connections/{id}", user);

        Assert.DoesNotContain(Daemon.Calls, c => c.Method is "unregister" or "deleteLocalAccountData"
            && c.Params.GetProperty("account").GetString() == number);
        Assert.Equal("their-connection", (await Claim(number).GetAsync())!.ConnectionId);
    }

    // Critic m2: switching to another number releases the earlier, never-verified one.
    [Fact]
    public async Task Register_AnotherNumber_ReleasesThePreviousPendingClaim()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var (first, second) = (NewNumber(), NewNumber());

        await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number = first });
        var switched = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number = second });

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Null(await Claim(first).GetAsync());
        Assert.Equal(id, (await Claim(second).GetAsync())!.ConnectionId);
    }

    // A claim that expired and was taken while the user fetched the code: 409, and the daemon is never asked to verify.
    [Fact]
    public async Task Verify_AfterTheClaimWasTakenOver_Is409_AndNeverCallsVerify()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var number = NewNumber();
        await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });
        await Claim(number).TryClaimAsync("someone-else", "their-connection", DateTimeOffset.UtcNow + ChatGatewayLimits.SignalPendingClaimLifetime);

        var response = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/verify", user, new { code = "123456" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(Daemon.Calls, c => c.Method == "verify" && c.Params.GetProperty("account").GetString() == number);
    }

    private void AssertForgotten(string number)
    {
        var mine = Daemon.Calls.Where(c => c.Params.ValueKind == JsonValueKind.Object
            && c.Params.TryGetProperty("account", out var account) && account.GetString() == number).Select(c => c.Method).ToList();
        var unregister = mine.IndexOf("unregister");
        Assert.True(unregister >= 0 && mine.IndexOf("deleteLocalAccountData") > unregister, string.Join(",", mine));
    }

    private async Task<(string User, string Id, string Number)> NewVerifiedConnectionAsync()
    {
        var (user, id) = await NewSignalConnectionAsync();
        var number = NewNumber();
        await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/register", user, new { number });
        Daemon.Accounts[number] = "aaaaaaaa-0000-4000-8000-" + number[^12..];
        var verified = await SendAsync(HttpMethod.Post, $"/api/chat-connections/{id}/signal/verify", user, new { code = "111222" });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (user, id, number);
    }

    private async Task<(string User, string Id)> NewSignalConnectionAsync()
    {
        var user = NewUser();
        var created = await SendAsync(HttpMethod.Post, "/api/chat-connections", user, new { platform = "Signal", name = "Signal" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ChatConnectionStatus.NeedsRegistration, view.GetProperty("status").GetString());
        return (user, view.GetProperty("id").GetString()!);
    }

    private ISignalNumberDirectoryGrain Claim(string number) => Cluster.GrainFactory.GetGrain<ISignalNumberDirectoryGrain>(number);

    // A fresh, valid E.164 number per test: the stub and the claim grains are shared across the class.
    private static string NewNumber() => "+3160" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString("D7");

    private static string Spaced(string number) => number[..3] + " " + number[3..5] + " " + number[5..];

    private static string NewUser() => "signal-api-" + Guid.NewGuid().ToString("N");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Fixture.Client.SendAsync(request);
    }
}
