namespace DvrManager.Models;

public sealed record Camera
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string RtspUrl { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string RtspTransport { get; init; } = "auto";
    public string StoragePath { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int SegmentMinutes { get; init; } = 15;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CameraRequest(
    string Name,
    string RtspUrl,
    string? Username,
    string? Password,
    bool Enabled = true,
    int SegmentMinutes = 15,
    bool ClearPassword = false,
    string RtspTransport = "auto",
    string? StoragePath = null)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Name))
            errors[nameof(Name)] = ["Informe o nome da câmera."];

        if (!Uri.TryCreate(RtspUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            errors[nameof(RtspUrl)] = ["Informe uma URL RTSP válida."];

        if (SegmentMinutes is < 1 or > 1440)
            errors[nameof(SegmentMinutes)] = ["O segmento deve ter entre 1 e 1440 minutos."];

        if (RtspTransport is not ("auto" or "tcp" or "udp"))
            errors[nameof(RtspTransport)] = ["O transporte RTSP deve ser automático, TCP ou UDP."];

        if (StoragePath?.IndexOf('\0') >= 0)
            errors[nameof(StoragePath)] = ["O diretório de armazenamento é inválido."];

        return errors;
    }

    public Camera ToCamera(Guid? id = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = Name.Trim(),
        RtspUrl = RtspUrl.Trim(),
        Username = Username?.Trim() ?? string.Empty,
        Password = Password ?? string.Empty,
        RtspTransport = RtspTransport,
        StoragePath = StoragePath?.Trim() ?? string.Empty,
        Enabled = Enabled,
        SegmentMinutes = SegmentMinutes,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

public sealed record CameraResponse(
    Guid Id,
    string Name,
    string RtspUrl,
    string Username,
    bool HasPassword,
    string RtspTransport,
    string StoragePath,
    bool Enabled,
    int SegmentMinutes,
    DateTimeOffset CreatedAt,
    RecordingStatus Recording)
{
    public static CameraResponse From(Camera camera, RecordingStatus status) => new(
        camera.Id,
        camera.Name,
        camera.RtspUrl,
        camera.Username,
        !string.IsNullOrEmpty(camera.Password),
        camera.RtspTransport,
        camera.StoragePath,
        camera.Enabled,
        camera.SegmentMinutes,
        camera.CreatedAt,
        status);
}
