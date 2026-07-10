using System.Diagnostics;
using System.Net.Http;

namespace Valerie.Services.Llm;

/// <summary>
/// If local Ollama isn't running, start it (ollama serve / ollama app) and wait until
/// http://localhost:11434 answers. Keeps V from dying just because the service was cold.
/// </summary>
public static class OllamaBootstrap
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    /// <summary>
    /// Returns true if Ollama is healthy (was already up, or we started it and it came up).
    /// Never throws for "not running" — only returns false if we couldn't bring it online.
    /// </summary>
    public static async Task<bool> EnsureRunningAsync(
        string baseUrl = DefaultBaseUrl,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        log ??= _ => { };
        timeout ??= TimeSpan.FromSeconds(45);

        if (await IsHealthyAsync(baseUrl).ConfigureAwait(false))
        {
            log("Ollama already up.");
            return true;
        }

        // Only auto-start for local endpoints
        if (!IsLocalUrl(baseUrl))
        {
            log($"Endpoint {baseUrl} is remote — won't try to start Ollama here.");
            return false;
        }

        log("Ollama not responding — starting it…");

        if (!TryStartOllama(log))
        {
            log("Couldn't find or launch ollama.exe. Install Ollama or start it manually.");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout.Value;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            if (await IsHealthyAsync(baseUrl).ConfigureAwait(false))
            {
                log($"Ollama is up (after {attempt} probe{(attempt == 1 ? "" : "s")}).");
                return true;
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }

        log($"Ollama still not answering at {baseUrl} after {timeout.Value.TotalSeconds:0}s.");
        return false;
    }

    public static bool IsLocalUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host is "localhost" or "127.0.0.1" or "::1";
    }

    public static async Task<bool> IsHealthyAsync(string baseUrl, int timeoutMs = 2500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartOllama(Action<string> log)
    {
        // Already a process? Give it a moment (might still be binding the port).
        try
        {
            var existing = Process.GetProcessesByName("ollama");
            if (existing.Length > 0)
            {
                log($"Found ollama process (pid {existing[0].Id}) — waiting for it to listen…");
                return true;
            }
        }
        catch { /* ignore */ }

        var exe = ResolveOllamaExe();
        if (exe is null)
        {
            log("ollama.exe not on PATH and not in the usual install folder.");
            return false;
        }

        try
        {
            // `ollama serve` is the headless server. On Windows the tray app also works;
            // serve is more reliable when launched from a console app.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            if (p is null)
            {
                log("Process.Start returned null for ollama serve.");
                return false;
            }
            log($"Started: {exe} serve (pid {p.Id})");
            // Don't wait on the process — it stays up as the server.
            return true;
        }
        catch (Exception ex)
        {
            log($"Failed to start ollama: {ex.Message}");
            // Last resort: open the GUI app (UseShellExecute)
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
                log("Launched ollama via shell as fallback.");
                return true;
            }
            catch (Exception ex2)
            {
                log($"Shell launch also failed: {ex2.Message}");
                return false;
            }
        }
    }

    private static string? ResolveOllamaExe()
    {
        // PATH first
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip bad path entries */ }
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        if (File.Exists(local)) return local;

        var pf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ollama", "ollama.exe");
        if (File.Exists(pf)) return pf;

        return null;
    }
}
