namespace WebApp.Options;

/// <summary>
/// Параметры конфигурации middleware логирования запросов.
/// </summary>
public sealed class RequestLoggingOptions
{
    /// <summary>
    /// Максимальный размер тела ответа для логирования при ошибке (в байтах).
    /// По умолчанию 64 KB.
    /// </summary>
    public int MaxResponseBodySizeBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Заголовки, которые маскируются в логах.
    /// Защита от утечки токенов, cookie и других секретов.
    /// </summary>
    public IReadOnlySet<string> SensitiveHeaders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Auth-Token",
        "Proxy-Authorization",
    };

    /// <summary>
    /// Content-Type ответа, при которых буферизация отключается.
    /// Для этих типов данные уходят клиенту немедленно (pass-through),
    /// тело ответа не логируется даже при ошибке.
    /// </summary>
    public IReadOnlySet<string> NonBufferableContentTypes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text/event-stream",        // SSE
        "application/octet-stream", // File download / binary stream
        "application/grpc",         // gRPC
    };

    /// <summary>
    /// Префиксы Content-Type ответа, при которых буферизация отключается.
    /// </summary>
    public IReadOnlyList<string> NonBufferableContentTypePrefixes { get; init; } = new List<string>
    {
        "video/",
        "audio/",
        "multipart/",
    };

    /// <summary>
    /// Пути, полностью исключённые из логирования.
    /// </summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = new List<string>
    {
        "/health",
        "/healthz",
        "/ping",
        "/metrics",
        "/favicon.ico",
    };
}
