using DvrManager.Models;

namespace DvrManager.Services;

public sealed class RecordingWorker(
    CameraRepository repository,
    RecordingManager recordings,
    ILogger<RecordingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartEnabledCamerasAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var cameraId in recordings.GetSessionsFromPreviousDays())
                {
                    var camera = await repository.GetAsync(cameraId);
                    if (camera is { Enabled: true }) await recordings.StartAsync(camera);
                }

                recordings.EnforceStorageLimit();

                var cameras = await repository.GetAllAsync();
                recordings.RegisterStoragePaths(cameras);
                foreach (var camera in cameras.Where(item => item.Enabled && recordings.GetStatus(item.Id).State == "error"))
                    await recordings.StartAsync(camera);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha na rotina de manutenção das gravações.");
            }
        }
    }

    private async Task StartEnabledCamerasAsync()
    {
        var cameras = await repository.GetAllAsync();
        recordings.RegisterStoragePaths(cameras);
        foreach (Camera camera in cameras.Where(item => item.Enabled))
            await recordings.StartAsync(camera);
    }
}
