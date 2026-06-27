using System.Diagnostics;
using DvrManager.Models;
using Microsoft.Extensions.Options;

namespace DvrManager.Services;

public sealed class LiveStreamManager : IDisposable
{
    private sealed record LiveSession(Guid CameraId, Process Process);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _ffmpegPath;
    private readonly ILogger<LiveStreamManager> _logger;
    private LiveSession? _activeSession;

    public LiveStreamManager(IOptions<DvrOptions> options, ILogger<LiveStreamManager> logger)
    {
        _ffmpegPath = ResolveFfmpegPath(options.Value.FfmpegPath);
        _logger = logger;
    }

    public async Task StreamAsync(Camera camera, Stream output, CancellationToken cancellationToken)
    {
        var process = await StartSessionAsync(camera, cancellationToken);

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The browser closed or changed the live view.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Transmissao ao vivo encerrada para a camera {CameraId}", camera.Id);
        }
        finally
        {
            await StopIfCurrentAsync(camera.Id, process);
        }
    }

    private async Task<Process> StartSessionAsync(Camera camera, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSession is not null)
                await StopProcessAsync(_activeSession.Process);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("warning");
            startInfo.ArgumentList.Add("-fflags");
            startInfo.ArgumentList.Add("+genpts");
            startInfo.ArgumentList.Add("-use_wallclock_as_timestamps");
            startInfo.ArgumentList.Add("1");
            if (camera.RtspTransport is "tcp" or "udp")
            {
                startInfo.ArgumentList.Add("-rtsp_transport");
                startInfo.ArgumentList.Add(camera.RtspTransport);
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(BuildStreamUrl(camera));
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0?");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0?");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("copy");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("96k");
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("frag_keyframe+empty_moov+default_base_moof");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add("pipe:1");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("O FFmpeg nao pode iniciar a transmissao ao vivo.");

            _activeSession = new LiveSession(camera.Id, process);
            _ = DrainErrorAsync(camera.Id, process);
            return process;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopIfCurrentAsync(Guid cameraId, Process process)
    {
        await _gate.WaitAsync();
        try
        {
            if (_activeSession is not { } session ||
                session.CameraId != cameraId ||
                !ReferenceEquals(session.Process, process))
                return;

            _activeSession = null;
            await StopProcessAsync(process);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Falha ao encerrar transmissao ao vivo.");
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task DrainErrorAsync(Guid cameraId, Process process)
    {
        try
        {
            var error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(error))
                _logger.LogWarning("FFmpeg live camera {CameraId}: {Output}", cameraId, error.Trim());
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Saida de erro da transmissao ao vivo encerrada.");
        }
    }

    private static string BuildStreamUrl(Camera camera)
    {
        if (string.IsNullOrEmpty(camera.Username)) return camera.RtspUrl;
        var uri = new UriBuilder(camera.RtspUrl) { UserName = camera.Username, Password = camera.Password };
        return uri.Uri.AbsoluteUri;
    }

    private static string ResolveFfmpegPath(string configuredPath)
    {
        if (!string.Equals(configuredPath, "ffmpeg", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
            return configuredPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesFolder = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packagesFolder)) return configuredPath;

        try
        {
            return Directory.EnumerateFiles(packagesFolder, "ffmpeg.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? configuredPath;
        }
        catch
        {
            return configuredPath;
        }
    }

    public void Dispose()
    {
        var session = _activeSession;
        _activeSession = null;
        if (session is not null)
            StopProcessAsync(session.Process).GetAwaiter().GetResult();
    }
}
