using System.Text.Json;
using DvrManager.Models;

namespace DvrManager.Services;

public sealed class CameraRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public CameraRepository(IHostEnvironment environment)
    {
        var dataFolder = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataFolder);
        _filePath = Path.Combine(dataFolder, "cameras.json");
    }

    public async Task<IReadOnlyList<Camera>> GetAllAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadUnsafeAsync(); }
        finally { _gate.Release(); }
    }

    public async Task<Camera?> GetAsync(Guid id) => (await GetAllAsync()).FirstOrDefault(camera => camera.Id == id);

    public async Task AddAsync(Camera camera)
    {
        await MutateAsync(items => items.Add(camera));
    }

    public async Task UpdateAsync(Camera camera)
    {
        await MutateAsync(items =>
        {
            var index = items.FindIndex(item => item.Id == camera.Id);
            if (index >= 0) items[index] = camera;
        });
    }

    public async Task DeleteAsync(Guid id)
    {
        await MutateAsync(items => items.RemoveAll(camera => camera.Id == id));
    }

    private async Task MutateAsync(Action<List<Camera>> mutation)
    {
        await _gate.WaitAsync();
        try
        {
            var items = await ReadUnsafeAsync();
            mutation(items);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tempPath, _filePath, true);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<Camera>> ReadUnsafeAsync()
    {
        if (!File.Exists(_filePath)) return [];
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<Camera>>(stream, JsonOptions) ?? [];
    }
}
