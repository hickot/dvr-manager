using DvrManager.Models;
using DvrManager.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DvrOptions>(builder.Configuration.GetSection("Dvr"));
builder.Services.AddSingleton<CameraRepository>();
builder.Services.AddSingleton<RecordingManager>();
builder.Services.AddHostedService<RecordingWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var cameras = app.MapGroup("/api/cameras");

cameras.MapGet("/", async (CameraRepository repository, RecordingManager recordings) =>
{
    var items = await repository.GetAllAsync();
    return Results.Ok(items.Select(camera => CameraResponse.From(camera, recordings.GetStatus(camera.Id))));
});

cameras.MapGet("/{id:guid}", async (Guid id, CameraRepository repository, RecordingManager recordings) =>
{
    var camera = await repository.GetAsync(id);
    return camera is null
        ? Results.NotFound()
        : Results.Ok(CameraResponse.From(camera, recordings.GetStatus(camera.Id)));
});

cameras.MapPost("/", async (CameraRequest request, CameraRepository repository, RecordingManager recordings) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var camera = request.ToCamera();
    await repository.AddAsync(camera);

    if (camera.Enabled)
    {
        await recordings.StartAsync(camera);
    }

    return Results.Created($"/api/cameras/{camera.Id}", CameraResponse.From(camera, recordings.GetStatus(camera.Id)));
});

cameras.MapPut("/{id:guid}", async (Guid id, CameraRequest request, CameraRepository repository, RecordingManager recordings) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var existing = await repository.GetAsync(id);
    if (existing is null)
    {
        return Results.NotFound();
    }

    var updated = request.ToCamera(id, existing.CreatedAt);
    if (request.Password is null && !request.ClearPassword)
    {
        updated = updated with { Password = existing.Password };
    }
    await recordings.StopAsync(id);
    await repository.UpdateAsync(updated);

    if (updated.Enabled)
    {
        await recordings.StartAsync(updated);
    }

    return Results.Ok(CameraResponse.From(updated, recordings.GetStatus(id)));
});

cameras.MapDelete("/{id:guid}", async (Guid id, CameraRepository repository, RecordingManager recordings) =>
{
    if (await repository.GetAsync(id) is null)
    {
        return Results.NotFound();
    }

    await recordings.StopAsync(id);
    await repository.DeleteAsync(id);
    return Results.NoContent();
});

cameras.MapPost("/{id:guid}/start", async (Guid id, CameraRepository repository, RecordingManager recordings) =>
{
    var camera = await repository.GetAsync(id);
    if (camera is null)
    {
        return Results.NotFound();
    }

    await recordings.StartAsync(camera);
    return Results.Ok(recordings.GetStatus(id));
});

cameras.MapPost("/{id:guid}/stop", async (Guid id, CameraRepository repository, RecordingManager recordings) =>
{
    if (await repository.GetAsync(id) is null)
    {
        return Results.NotFound();
    }

    await recordings.StopAsync(id);
    return Results.Ok(recordings.GetStatus(id));
});

app.MapGet("/api/system", (RecordingManager recordings) => Results.Ok(recordings.GetSystemStatus()));

app.MapGet("/api/directories", (string? path) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new DirectoryItem(drive.Name, drive.Name))
                .ToList();
            return Results.Ok(new DirectoryListing(null, null, roots));
        }

        var currentPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!Directory.Exists(currentPath))
            return Results.NotFound(new { detail = "O diretório informado não existe." });

        var parentPath = Directory.GetParent(currentPath)?.FullName;
        var directories = Directory.EnumerateDirectories(currentPath)
            .Select(directory => new DirectoryInfo(directory))
            .Where(directory => !directory.Attributes.HasFlag(FileAttributes.System))
            .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .Select(directory => new DirectoryItem(directory.Name, directory.FullName))
            .ToList();

        return Results.Ok(new DirectoryListing(currentPath, parentPath, directories));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem("A aplicação não possui permissão para acessar esse diretório.", statusCode: 403);
    }
    catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
    {
        return Results.BadRequest(new { detail = "Não foi possível abrir esse diretório." });
    }
});

app.MapFallbackToFile("index.html");
app.Run();

internal sealed record DirectoryItem(string Name, string Path);
internal sealed record DirectoryListing(string? CurrentPath, string? ParentPath, IReadOnlyList<DirectoryItem> Directories);
