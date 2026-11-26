using System.IO;
using IdleDeck.Core;
using IdleDeck.Core.Settings;
using Microsoft.Web.WebView2.Core;

namespace IdleDeck.App.Browser;

/// <summary>
/// O único ambiente WebView2 do app. Todas as contas são perfis dentro dele, então
/// compartilham o processo do navegador, a GPU e a rede.
/// </summary>
public sealed class BrowserHost
{
    private static readonly string[] BaseArguments =
    [
        "--disable-features=Translate,AutofillServerCommunication,SpellcheckService,msSmartScreenProtection",
    ];

    private static readonly string[] NoThrottlingArguments =
    [
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
    ];

    /// <summary>Tudo desenhado pela CPU: o processo da GPU fica com ~20 MB em vez de 450+ MB que crescem com as horas.</summary>
    private static readonly string[] NoGpuArguments =
    [
        "--disable-gpu",
        "--disable-gpu-compositing",
    ];

    private BrowserHost(CoreWebView2Environment environment, bool throttlingDisabled, bool extensionsEnabled, bool gpuEnabled)
    {
        Environment = environment;
        ThrottlingDisabled = throttlingDisabled;
        ExtensionsEnabled = extensionsEnabled;
        GpuEnabled = gpuEnabled;
    }

    public CoreWebView2Environment Environment { get; }

    /// <summary>Valores com que o ambiente foi criado; mudá-los exige reiniciar o app.</summary>
    public bool ThrottlingDisabled { get; }
    public bool ExtensionsEnabled { get; }
    public bool GpuEnabled { get; }

    public static async Task<BrowserHost> CreateAsync(AppSettings settings)
    {
        var args = settings.DisableBackgroundThrottling ? BaseArguments.Concat(NoThrottlingArguments) : BaseArguments;
        if (!settings.UseGpu) args = args.Concat(NoGpuArguments);
        var options = new CoreWebView2EnvironmentOptions(string.Join(' ', args))
        {
            AreBrowserExtensionsEnabled = settings.ExtensionsEnabled,
        };
        var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData, options);
        return new BrowserHost(env, settings.DisableBackgroundThrottling, settings.ExtensionsEnabled, settings.UseGpu);
    }

    /// <summary>IDs das extensões que o próprio app instalou (só essas ele remove; as internas do navegador ficam).</summary>
    private static string LoadedIdsFile => Path.Combine(AppPaths.Root, "extensions-loaded.txt");

    /// <summary>
    /// Carrega no perfil da conta as extensões de <see cref="AppPaths.ExtensionsDir"/> (cada subpasta com manifest.json).
    /// Recarrega a cada abertura, para o perfil sempre usar o código que está na pasta. Extensões que o app instalou antes
    /// e não estão mais na pasta são removidas. Só roda com "Permitir extensões do navegador" ligado.
    /// </summary>
    public async Task LoadExtensionsAsync(CoreWebView2Profile profile)
    {
        if (!ExtensionsEnabled) return;
        try
        {
            Directory.CreateDirectory(AppPaths.ExtensionsDir);
            var folders = Directory.GetDirectories(AppPaths.ExtensionsDir)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .ToList();

            var ours = File.Exists(LoadedIdsFile)
                ? File.ReadAllLines(LoadedIdsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
                : [];
            foreach (var installed in await profile.GetBrowserExtensionsAsync())
            {
                if (!ours.Contains(installed.Id)) continue;
                try { await installed.RemoveAsync(); }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { App.Log(e); }
            }

            foreach (var folder in folders)
            {
                try
                {
                    var added = await profile.AddBrowserExtensionAsync(folder);
                    ours.Add(added.Id);
                }
                catch (Exception e) when (e is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    App.Log(new InvalidOperationException($"Extensão não carregou: {folder}", e));
                }
            }
            File.WriteAllLines(LoadedIdsFile, ours);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            App.Log(e);
        }
    }
}
