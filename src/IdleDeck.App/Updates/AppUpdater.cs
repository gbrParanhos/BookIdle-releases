using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using IdleDeck.Core;
using IdleDeck.Core.Updates;

namespace IdleDeck.App.Updates;

/// <summary>A versão deste BookIdle (a que foi gravada no executável com -p:Version=x.y.z).</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var text = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = text.IndexOf('+'); // "1.0.1+abc123" → "1.0.1"
        return plus > 0 ? text[..plus] : text;
    }
}

/// <summary>Uma versão publicada no GitHub: o número e a página dela.</summary>
public sealed record AvailableUpdate(string Version, string PageUrl, string? NotesMarkdown);

/// <summary>Uma extensão da pasta extensions com versão nova na Chrome Web Store.</summary>
public sealed record ExtensionUpdate(string Name, string Installed, string Latest, string StoreUrl);

/// <summary>
/// Build própria: SÓ AVISA. Consulta a última release do repositório no GitHub e diz se há versão nova;
/// nunca baixa nem instala nada. Para atualizar, revise o código novo e compile de novo.
/// </summary>
public sealed class AppUpdater
{
    /// <summary>O repositório (público) onde as versões são publicadas.</summary>
    public const string RepoUrl = "https://github.com/PatrickKenji/BookIdle-releases";
    private const string LatestApi = "https://api.github.com/repos/PatrickKenji/BookIdle-releases/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookIdle-update-check");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>A versão nova, ou null se esta já é a mais recente.</summary>
    public async Task<AvailableUpdate?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v', 'V');
        if (!Version.TryParse(latest, out var latestVersion)) return null;
        if (Version.TryParse(AppVersion.Current, out var current) && latestVersion <= current) return null;

        var page = root.TryGetProperty("html_url", out var url) && url.GetString() is { } u
                   && u.StartsWith(RepoUrl + "/", StringComparison.OrdinalIgnoreCase)
            ? u
            : RepoUrl + "/releases";
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        return new AvailableUpdate(latest, page, notes);
    }

    // ---------- Extensões (pasta extensions) ----------

    /// <summary>Arquivo, dentro da pasta da extensão, com o ID dela na Chrome Web Store (32 letras a–p).</summary>
    public const string StoreIdFile = "store-id.txt";
    private const string StoreCheckUrl =
        "https://clients2.google.com/service/update2/crx?response=updatecheck&prodversion=131.0&acceptformat=crx2,crx3&x=id%3D{0}%26v%3D0.0.0%26uc";

    /// <summary>
    /// Extensões da pasta <see cref="AppPaths.ExtensionsDir"/> que têm versão nova na Chrome Web Store. Só consulta a
    /// versão publicada; nunca baixa a extensão. Pastas sem <see cref="StoreIdFile"/> ficam de fora. Uma extensão que
    /// falhar não impede as outras; se TODAS falharem, lança o erro (para a verificação manual mostrar).
    /// </summary>
    public async Task<List<ExtensionUpdate>> CheckExtensionsAsync()
    {
        var result = new List<ExtensionUpdate>();
        if (!Directory.Exists(AppPaths.ExtensionsDir)) return result;
        Exception? lastError = null;
        var checkedAny = false;
        foreach (var folder in Directory.GetDirectories(AppPaths.ExtensionsDir))
        {
            var idFile = Path.Combine(folder, StoreIdFile);
            var manifestFile = Path.Combine(folder, "manifest.json");
            if (!File.Exists(idFile) || !File.Exists(manifestFile)) continue;
            try
            {
                var id = (await File.ReadAllTextAsync(idFile)).Trim().ToLowerInvariant();
                if (id.Length != 32 || id.Any(c => c is < 'a' or > 'p')) continue;
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));
                var name = manifest.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                var installed = manifest.RootElement.GetProperty("version").GetString() ?? "0";

                var xml = System.Xml.Linq.XDocument.Parse(await Http.GetStringAsync(string.Format(StoreCheckUrl, id)));
                var latest = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "updatecheck")?.Attribute("version")?.Value;
                checkedAny = true;
                if (latest is not null && Version.TryParse(latest, out var lv) && Version.TryParse(installed, out var iv) && lv > iv)
                    result.Add(new ExtensionUpdate(name, installed, latest, "https://chromewebstore.google.com/detail/" + id));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException
                                          or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException)
            {
                lastError = e;
            }
        }
        if (!checkedAny && lastError is not null) throw lastError;
        return result;
    }

    /// <summary>Notas da versão nova, tiradas do texto da release.</summary>
    public static ReleaseNotes? NotesOf(AvailableUpdate update) => ReleaseNotes.Parse(update.NotesMarkdown, Loc.Code);

    /// <summary>Notas desta versão, embutidas no executável (<c>release-notes/X.Y.Z.md</c>).</summary>
    public static ReleaseNotes? CurrentNotes()
    {
        using var stream = typeof(AppUpdater).Assembly.GetManifestResourceStream($"ReleaseNotes.{AppVersion.Current}.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return ReleaseNotes.Parse(reader.ReadToEnd(), Loc.Code);
    }
}
