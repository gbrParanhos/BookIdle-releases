using System.Net.Http;
using System.Text.Json;

namespace IdleDeck.Core.Kick;

/// <summary>
/// Leitura (só leitura) da Kick pela mesma API que o site usa: quem está ao vivo (público), o saldo de pontos da conta num
/// canal e o nome de usuário (com a sessão da conta).
/// </summary>
public static class KickApi
{
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan LiveCacheFor = TimeSpan.FromSeconds(45);
    /// <summary>Se a Kick não responder (ex.: 429), vale a última resposta de até este tempo atrás.</summary>
    private static readonly TimeSpan LiveStaleFor = TimeSpan.FromMinutes(10);
    /// <summary>Saldo de canal ao vivo: sobe +10 a cada 5 min assistindo, então 3 min de cache não perdem nada.</summary>
    private static readonly TimeSpan PointsLiveCacheFor = TimeSpan.FromMinutes(3);
    /// <summary>Saldo de canal fora do ar não muda sozinho.</summary>
    private static readonly TimeSpan PointsOfflineCacheFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PointsStaleFor = TimeSpan.FromHours(1);
    private static readonly Dictionary<string, (DateTime At, bool Live)> LiveCache = [];
    private static readonly Dictionary<(string Token, string Channel), (DateTime At, int? Points)> PointsCache = [];
    private static readonly SemaphoreSlim LiveGate = new(1, 1);
    /// <summary>Todas as consultas à Kick passam por aqui, uma por vez (<see cref="KickThrottle"/>).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly KickThrottle Throttle = new(TimeSpan.FromMilliseconds(350));

    /// <summary>Quanto falta da pausa que a Kick pediu (429); zero = consultando normalmente.</summary>
    public static TimeSpan PausedFor
    {
        get { lock (Throttle) return Throttle.PausedFor(DateTime.UtcNow); }
    }

    /// <summary>Resposta de <c>/api/v2/channels/&lt;canal&gt;</c>: ao vivo quando <c>livestream</c> é um objeto.</summary>
    public static bool ParseLive(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("slug", out _))
            throw new FormatException("Resposta da Kick sem o canal: " + Short(json));
        return root.TryGetProperty("livestream", out var ls) && ls.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Resposta de <c>/api/v2/channels/&lt;canal&gt;/points</c>; null sem sessão/sem dados.</summary>
    public static int? ParsePoints(string json)
    {
        using var doc = Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : null;
    }

    /// <summary>Resposta de <c>/api/v1/user</c>; null sem sessão. (A resposta traz o e-mail: só o nome sai daqui.)</summary>
    public static string? ParseUsername(string json)
    {
        using var doc = Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() : null;
    }

    /// <summary>
    /// Quais canais estão ao vivo. A resposta de cada canal vale por 45 s para todas as contas (a pergunta é pública).
    /// Se a Kick não responder, vale a última resposta dos últimos 10 min (assim um 429 não fecha as lives abertas); sem
    /// ela, o canal conta como fora do ar nesta rodada e o erro vai para <paramref name="errors"/> (a pausa da Kick, uma vez só).
    /// </summary>
    public static async Task<HashSet<string>> ReadLiveAsync(IReadOnlyList<string> channels, List<string> errors, CancellationToken ct = default)
    {
        var live = new HashSet<string>();
        var busyReported = false;
        await LiveGate.WaitAsync(ct);
        try
        {
            foreach (var channel in channels)
            {
                bool hasCached;
                (DateTime At, bool Live) cached;
                lock (LiveCache) hasCached = LiveCache.TryGetValue(channel, out cached);
                if (hasCached && DateTime.UtcNow - cached.At < LiveCacheFor)
                {
                    if (cached.Live) live.Add(channel);
                    continue;
                }
                try
                {
                    var isLive = ParseLive(await GetAsync($"https://kick.com/api/v2/channels/{channel}", null, ct));
                    lock (LiveCache) LiveCache[channel] = (DateTime.UtcNow, isLive);
                    if (isLive) live.Add(channel);
                }
                catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (hasCached && DateTime.UtcNow - cached.At < LiveStaleFor)
                    {
                        if (cached.Live) live.Add(channel);
                    }
                    else if (e is KickBusyException)
                    {
                        if (!busyReported) errors.Add(e.Message);
                        busyReported = true;
                    }
                    else errors.Add($"{channel}: {e.Message}");
                }
            }
        }
        finally { LiveGate.Release(); }
        return live;
    }

    /// <summary>
    /// Saldo da conta no canal. Fica guardado por 3 min (30 min se o canal está fora do ar, quando não muda); se a Kick
    /// não responder, vale o último saldo da última hora. Sem nenhum, o erro sobe (<see cref="KickBusyException"/> no 429).
    /// </summary>
    public static async Task<int?> ReadPointsAsync(string token, string channel, CancellationToken ct = default)
    {
        var key = (token, channel);
        bool hasCached;
        (DateTime At, int? Points) cached;
        bool knownOffline;
        lock (PointsCache) hasCached = PointsCache.TryGetValue(key, out cached);
        lock (LiveCache) knownOffline = LiveCache.TryGetValue(channel, out var l) && !l.Live;
        var freshFor = knownOffline ? PointsOfflineCacheFor : PointsLiveCacheFor;
        if (hasCached && DateTime.UtcNow - cached.At < freshFor) return cached.Points;
        try
        {
            var points = ParsePoints(await GetAsync($"https://kick.com/api/v2/channels/{channel}/points", token, ct));
            lock (PointsCache) PointsCache[key] = (DateTime.UtcNow, points);
            return points;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested
                                  && hasCached && cached.Points is not null && DateTime.UtcNow - cached.At < PointsStaleFor)
        {
            return cached.Points;
        }
    }

    public static async Task<string?> ReadUsernameAsync(string token, CancellationToken ct = default) =>
        ParseUsername(await GetAsync("https://kick.com/api/v1/user", token, ct));

    private static async Task<string> GetAsync(string url, string? token, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            TimeSpan paused, wait;
            lock (Throttle)
            {
                paused = Throttle.PausedFor(DateTime.UtcNow);
                wait = Throttle.WaitBeforeNext(DateTime.UtcNow);
            }
            if (paused > TimeSpan.Zero) throw new KickBusyException(paused);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (Throttle) Throttle.MarkSent(DateTime.UtcNow);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", Agent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await Http.SendAsync(request, ct);
            if ((int)response.StatusCode == 429)
            {
                TimeSpan pause;
                lock (Throttle) pause = Throttle.OnTooMany(DateTime.UtcNow, RetryAfter(response));
                throw new KickBusyException(pause);
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode is 401 or 403 && token is not null && !text.TrimStart().StartsWith('<'))
                return text; // sem sessão: o chamador vê "sem dados"
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Kick respondeu {(int)response.StatusCode}");
            return text;
        }
        finally { Gate.Release(); }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormatException("Resposta da Kick inválida: " + Short(json), e); }
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;
}
