using System.Net.Http;
using System.Text.Json;
using System.Windows;
using IdleDeck.Core.Kick;
using IdleDeck.Core.Lives;
using IdleDeck.Core.Twitch;

namespace IdleDeck.App.Modules.Lives;

/// <summary>
/// Janela "Lives" (só visual): todos os canais da Twitch e da Kick do perfil, quem está ao vivo, o saldo de pontos de
/// cada conta em cada canal e quais lives cada conta está assistindo agora. Só lê (a mesma API que os sites usam);
/// não mexe nas lives nem abre páginas.
/// </summary>
public sealed class LivesModule(IModuleHostServices host) : IModuleView
{
    private LivesPanel? _panel;

    public string Id => "lives";
    public string Title => Loc.T("Pontos das Lives");
    public string UrlPattern => "pokeidle.io";
    public IReadOnlyList<string> AllowedPaths => ["/", "/app"];
    public string? InjectedScript => null;
    public string? Description => Loc.T("Canais da Twitch e da Kick do perfil: quem está ao vivo, quantos pontos cada conta tem em cada canal e o que ela está assistindo (▶).");
    public string? Summary => null;

#pragma warning disable CS0067 // sem resumo na lateral
    public event EventHandler? SummaryChanged;
#pragma warning restore CS0067

    public FrameworkElement CreatePanel() => _panel = new LivesPanel(this);

    public void OnMessage(int accountIndex, JsonElement data) { }

    /// <summary>
    /// Abre a página do canal (para resgatar pontos) numa janela logada com a conta da coluna
    /// <paramref name="accountIndex"/> da última tabela lida (mesma ordem de <see cref="ReadTwitchAsync"/>/<see cref="ReadKickAsync"/>).
    /// </summary>
    public void OpenChannel(bool kick, int accountIndex, string channel)
    {
        var accounts = host.ContasDasLives().Where(a => (kick ? a.KickLogin : a.TwitchLogin) is not null).ToList();
        if (accountIndex < 0 || accountIndex >= accounts.Count) return;
        var info = accounts[accountIndex];
        var url = kick ? "https://kick.com/" + Uri.EscapeDataString(channel) : "https://www.twitch.tv/" + Uri.EscapeDataString(channel);
        _ = host.AbrirPaginaNaContaAsync(info.ProfileName, $"{(kick ? "Kick" : "Twitch")} · {channel}", url);
    }

    public event EventHandler? ContasMudaram
    {
        add => host.ContasMudaram += value;
        remove => host.ContasMudaram -= value;
    }

    /// <summary>A tabela da Twitch: ao vivo pela API do site; saldos com a chave de cada conta conectada.</summary>
    public async Task<(LiveBoard Board, List<string> Errors)> ReadTwitchAsync()
    {
        var channels = host.CanaisDasLives().Twitch;
        var errors = new List<string>();
        var live = new HashSet<string>();
        var accounts = new List<LiveBoardAccount>();
        if (channels.Count == 0) return (LiveBoard.Build(channels, live, accounts), errors);

        var anyLiveRead = false;
        foreach (var info in host.ContasDasLives().Where(a => a.TwitchLogin is not null))
        {
            var balances = new Dictionary<string, int?>();
            try
            {
                var token = await host.ChaveTwitchAsync(info.ProfileName);
                if (token is null) errors.Add(Loc.T("{0}: sem login da Twitch no navegador (Reconectar Twitch)", info.Name));
                else
                {
                    foreach (var s in await TwitchGql.ReadChannelsAsync(token, channels))
                    {
                        balances[s.Channel] = s.Balance ?? 0;
                        if (s.Live) live.Add(s.Channel);
                    }
                    anyLiveRead = true;
                }
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException)
            {
                errors.Add($"{info.Name}: {e.Message}");
            }
            accounts.Add(new LiveBoardAccount(info.TwitchLogin!, balances, info.TwitchWatching.ToHashSet()));
        }
        if (!anyLiveRead)
        {
            try { live = (await TwitchGql.ReadChannelsAsync(null, channels)).Where(s => s.Live).Select(s => s.Channel).ToHashSet(); }
            catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException) { errors.Add("Twitch: " + e.Message); }
        }
        return (LiveBoard.Build(channels, live, accounts), errors);
    }

    /// <summary>A tabela da Kick: ao vivo pela API pública; saldos com a sessão de cada conta conectada.</summary>
    public async Task<(LiveBoard Board, List<string> Errors)> ReadKickAsync()
    {
        var channels = host.CanaisDasLives().Kick;
        var errors = new List<string>();
        var accounts = new List<LiveBoardAccount>();
        if (channels.Count == 0) return (LiveBoard.Build(channels, new HashSet<string>(), accounts), errors);

        var liveErrors = new List<string>();
        var live = await KickApi.ReadLiveAsync(channels, liveErrors);
        if (liveErrors.Count > 0) errors.Add(Loc.T("Kick: não deu para ver se estão ao vivo: {0}", string.Join(", ", liveErrors)));
        var missingByPause = 0;
        foreach (var info in host.ContasDasLives().Where(a => a.KickLogin is not null))
        {
            var balances = new Dictionary<string, int?>();
            var token = await host.ChaveKickAsync(info.ProfileName);
            if (token is null) errors.Add(Loc.T("{0}: sem login da Kick no navegador (Reconectar Kick)", info.Name));
            else
            {
                foreach (var channel in channels)
                {
                    try { balances[channel] = await KickApi.ReadPointsAsync(token, channel); }
                    catch (KickBusyException)
                    {
                        balances[channel] = null;
                        missingByPause++;
                    }
                    catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException)
                    {
                        balances[channel] = null;
                        errors.Add($"{info.Name} · {channel}: {e.Message}");
                    }
                }
            }
            accounts.Add(new LiveBoardAccount(info.KickLogin!, balances, info.KickWatching.ToHashSet()));
        }
        // A Kick barra quando chegam consultas demais (429): o app espera a pausa e mostra os últimos saldos que já leu.
        var paused = KickApi.PausedFor;
        if (paused > TimeSpan.Zero)
            errors.Add(missingByPause > 0
                ? Loc.T("Kick pediu uma pausa nas consultas; {0} saldo(s) aparecem na próxima atualização (em ~{1} s).", missingByPause, (int)Math.Ceiling(paused.TotalSeconds))
                : Loc.T("Kick pediu uma pausa nas consultas; mostrando os últimos saldos lidos."));
        return (LiveBoard.Build(channels, live, accounts), errors);
    }
}
