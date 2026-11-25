using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WhisperStt.Core;

// What Twitch hands out to start a sign-in: the user opens VerificationUri (the code is already in it)
// and approves there, while the app waits.
public sealed record TwitchDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

// A signed-in Twitch user. The access token lasts about four hours; the refresh token gets a new one
// without asking the user again, and is itself replaced every time it is used.
public sealed record TwitchSession(string AccessToken, string RefreshToken, DateTime ExpiresAt, string UserId, string Login);

// A problem Twitch reported, in words that can be shown to the user.
public sealed class TwitchException(string message, bool signInLost = false) : Exception(message)
{
    // True when the user has to connect again (the sign-in was revoked or expired for good).
    public bool SignInLost { get; } = signInLost;
}

// Twitch sign-in (OAuth 2 device code flow, for apps that cannot keep a secret) and sending chat messages
// through the Helix API. The app never sees the user's password.
public static class TwitchApi
{
    // The only permission asked for: write chat messages as the user.
    public const string Scopes = "user:write:chat";
    public const int MaxMessageLength = 500;

    // WalkTalk's own application ID from dev.twitch.tv (client type "Public"). An ID is not a secret.
    public const string BuiltInClientId = "nbbcsaxco6lx43wyb40ak65n6onfls";

    // Settable so tests can talk to a stand-in instead of Twitch.
    public static string AuthBase { get; set; } = "https://id.twitch.tv/oauth2";
    public static string ApiBase { get; set; } = "https://api.twitch.tv/helix";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<TwitchDeviceCode> StartSignInAsync(string clientId, CancellationToken ct = default)
    {
        using var response = await PostFormAsync($"{AuthBase}/device", ct, ("client_id", clientId), ("scopes", Scopes)).ConfigureAwait(false);
        var json = await ReadAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TwitchException(response.StatusCode == HttpStatusCode.BadRequest
                ? $"Twitch refused the application ID: {Message(json)}. Check the Client ID, and that the app's client type is Public."
                : $"Twitch couldn't start the sign-in: {Message(json)}");
        return new TwitchDeviceCode(
            json.GetProperty("device_code").GetString()!, json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri").GetString()!, json.GetProperty("expires_in").GetInt32(),
            Math.Max(1, json.GetProperty("interval").GetInt32()));
    }

    // Asks Twitch every few seconds whether the user has approved, until they have or the code expires.
    public static async Task<TwitchSession> WaitForApprovalAsync(string clientId, TwitchDeviceCode code, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(code.Interval);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            using var response = await PostFormAsync($"{AuthBase}/token", ct, ("client_id", clientId), ("scopes", Scopes),
                ("device_code", code.DeviceCode), ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")).ConfigureAwait(false);
            var json = await ReadAsync(response, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return await SessionFromAsync(json, ct).ConfigureAwait(false);

            var message = Message(json);
            if (message.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                continue;
            if (message.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }
            throw new TwitchException($"Twitch didn't complete the sign-in: {message}");
        }
        throw new TwitchException("The sign-in code expired before it was approved. Press Connect to try again.");
    }

    public static async Task<TwitchSession> RefreshAsync(string clientId, TwitchSession session, CancellationToken ct = default)
    {
        using var response = await PostFormAsync($"{AuthBase}/token", ct, ("client_id", clientId),
            ("grant_type", "refresh_token"), ("refresh_token", session.RefreshToken)).ConfigureAwait(false);
        var json = await ReadAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new TwitchException("Your Twitch sign-in is no longer valid. Connect again.", signInLost: true);
        if (!response.IsSuccessStatusCode)
            throw new TwitchException($"Twitch couldn't renew the sign-in: {Message(json)}");
        return await SessionFromAsync(json, ct).ConfigureAwait(false);
    }

    // The numeric ID of a channel, from the name in its address (twitch.tv/<name>).
    public static async Task<string> FindChannelAsync(string clientId, TwitchSession session, string channel, CancellationToken ct = default)
    {
        // Twitch login names are lowercase; people often type them with capitals.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users?login={Uri.EscapeDataString(channel.ToLowerInvariant())}");
        using var response = await SendAsync(request, clientId, session, ct).ConfigureAwait(false);
        var json = await ReadAsync(response, ct).ConfigureAwait(false);
        Check(response, json);
        var data = json.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new TwitchException($"There is no Twitch channel called \"{channel}\".");
        return data[0].GetProperty("id").GetString()!;
    }

    public static async Task SendMessageAsync(string clientId, TwitchSession session, string channelId, string message, CancellationToken ct = default)
    {
        if (message.Length > MaxMessageLength)
            message = message[..(MaxMessageLength - 1)] + "…";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/chat/messages")
        {
            Content = JsonContent.Create(new { broadcaster_id = channelId, sender_id = session.UserId, message })
        };
        using var response = await SendAsync(request, clientId, session, ct).ConfigureAwait(false);
        var json = await ReadAsync(response, ct).ConfigureAwait(false);
        Check(response, json);

        // Twitch can accept the request and still not show the message (slow mode, a duplicate, AutoMod...).
        var result = json.GetProperty("data")[0];
        if (!result.GetProperty("is_sent").GetBoolean())
        {
            var reason = result.TryGetProperty("drop_reason", out var drop) && drop.ValueKind == JsonValueKind.Object
                ? drop.GetProperty("message").GetString()
                : null;
            throw new TwitchException($"Twitch didn't post it: {reason ?? "no reason given"}");
        }
    }

    // Tells Twitch to forget the sign-in. Best effort: the local copy is deleted either way.
    public static async Task SignOutAsync(string clientId, TwitchSession session)
    {
        try
        {
            using var _ = await PostFormAsync($"{AuthBase}/revoke", default, ("client_id", clientId), ("token", session.AccessToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    // The token response says nothing about who signed in; the validate call does.
    private static async Task<TwitchSession> SessionFromAsync(JsonElement token, CancellationToken ct)
    {
        var access = token.GetProperty("access_token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AuthBase}/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", access);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var who = await ReadAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TwitchException($"Twitch signed in but wouldn't say who: {Message(who)}");
        return new TwitchSession(access, token.GetProperty("refresh_token").GetString()!,
            DateTime.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32()),
            who.GetProperty("user_id").GetString()!, who.GetProperty("login").GetString()!);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string clientId, TwitchSession session, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("Client-Id", clientId);
        return Http.SendAsync(request, ct);
    }

    private static Task<HttpResponseMessage> PostFormAsync(string url, CancellationToken ct, params (string Key, string Value)[] fields) =>
        Http.PostAsync(url, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))), ct);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}"); // an error page instead of JSON
            return empty.RootElement.Clone();
        }
    }

    private static string Message(JsonElement json) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text
            ? text
            : "no details given";

    private static void Check(HttpResponseMessage response, JsonElement json)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw response.StatusCode switch
        {
            // 401 is the caller's cue to refresh the access token and try once more.
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException(Message(json)),
            HttpStatusCode.Forbidden => new TwitchException("You aren't allowed to write in that channel's chat (banned, timed out, or the chat is restricted)."),
            HttpStatusCode.TooManyRequests => new TwitchException("Twitch is limiting how fast you can post. Wait a few seconds."),
            (HttpStatusCode)422 => new TwitchException("The message is too long for Twitch chat."),
            _ => new TwitchException($"Twitch refused it: {Message(json)}")
        };
    }
}
