namespace IdleDeck.Core.Kick;

/// <summary>A Kick barrou por excesso de consultas (429) e ainda não passou a pausa que ela pediu.</summary>
public sealed class KickBusyException(TimeSpan wait)
    : System.Net.Http.HttpRequestException($"Kick pediu uma pausa (muitas consultas); tenta de novo em {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s")
{
    public TimeSpan Wait { get; } = wait;
}

/// <summary>
/// Ritmo das consultas à Kick, que barra o IP (429) quando chegam muitas de uma vez (o app consulta várias contas × 24
/// canais). Uma consulta por vez, com um intervalo mínimo entre elas; depois de um 429, ninguém consulta até passar a
/// pausa que a Kick pediu (Retry-After) ou, sem ela, um minuto. Só cálculo: quem chama espera e marca.
/// </summary>
public sealed class KickThrottle(TimeSpan spacing)
{
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinPause = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    private DateTime _lastSent = DateTime.MinValue;
    private DateTime _pausedUntil = DateTime.MinValue;

    /// <summary>Quanto falta da pausa pedida pela Kick (zero = pode consultar).</summary>
    public TimeSpan PausedFor(DateTime now) => _pausedUntil > now ? _pausedUntil - now : TimeSpan.Zero;

    /// <summary>Quanto esperar antes da próxima consulta para manter o intervalo mínimo.</summary>
    public TimeSpan WaitBeforeNext(DateTime now)
    {
        var next = _lastSent + spacing;
        return next > now ? next - now : TimeSpan.Zero;
    }

    public void MarkSent(DateTime now) => _lastSent = now;

    /// <summary>A Kick respondeu 429: pausa todas as consultas pelo tempo pedido (entre 10 s e 5 min).</summary>
    public TimeSpan OnTooMany(DateTime now, TimeSpan? retryAfter)
    {
        var pause = retryAfter ?? DefaultPause;
        if (pause < MinPause) pause = MinPause;
        if (pause > MaxPause) pause = MaxPause;
        if (now + pause > _pausedUntil) _pausedUntil = now + pause;
        return PausedFor(now);
    }
}
