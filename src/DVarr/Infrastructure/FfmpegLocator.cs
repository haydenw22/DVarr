using System.Diagnostics;

namespace DVarr.Infrastructure;

/// <summary>Resolves ffmpeg/ffprobe once at startup (configured path, then PATH, then common locations).</summary>
public sealed class FfmpegLocator
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }

    /// <summary>Set once at startup; surfaced on /api/health and the dashboard.</summary>
    public string? CachedVersion { get; set; }

    public FfmpegLocator(IConfiguration cfg, ILogger<FfmpegLocator> log)
    {
        Ffmpeg = Resolve(cfg["DVarr:FfmpegPath"], "ffmpeg");
        Ffprobe = Resolve(cfg["DVarr:FfprobePath"], "ffprobe");
        log.LogInformation("[ffmpeg] ffmpeg={F} ffprobe={P}", Ffmpeg, Ffprobe);
    }

    public async Task<string?> VersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg, "-hide_banner -version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? outp.Split('\n').FirstOrDefault()?.Trim() : null;
        }
        catch { return null; }
    }

    private static string Resolve(string? configured, string exe)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured!;

        var name = OperatingSystem.IsWindows() ? exe + ".exe" : exe;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH entry */ }
        }

        string[] common = OperatingSystem.IsWindows()
            ? new[] { @"C:\ffmpeg\bin\" + name }
            : new[] { "/usr/bin/" + exe, "/usr/local/bin/" + exe };
        foreach (var c in common)
            if (File.Exists(c)) return c;

        return exe; // last resort: let the OS resolve it at exec time
    }
}
