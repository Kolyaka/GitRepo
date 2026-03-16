namespace WebApp.Options;

/// <summary>
/// Параметры конфигурации middleware логирования запросов.
/// </summary>
public sealed class RequestLoggingOptions
{
    /// <summary>
    /// Максимальный размер тела запроса, который будет прочитан (в байтах).
    /// По умолчанию 64 KB. Защита от атак на память (oversized payload).
    /// </summary>
    public int MaxRequestBodySizeBytes { get; init; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Максимальный размер тела ответа, который будет прочитан при ошибке (в байтах).
    /// По умолчанию 64 KB.
    /// </summary>
    public int MaxResponseBodySizeBytes { get; init; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Список HTTP-заголовков, которые нужно маскировать в логах.
    /// Защита от утечки токенов, cookie и других чувствительных данных.
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
    /// Список Content-Type, при которых тело запроса будет логироваться.
    /// Только текстовые форматы — бинарные данные не логируются.
    /// </summary>
    public IReadOnlySet<string> LoggableContentTypes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/xml",
        "application/x-www-form-urlencoded",
        "text/plain",
        "text/xml",
    };

    /// <summary>
    /// Пути, которые исключаются из логирования (например, healthcheck).
    /// </summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = new List<string>
    {
        "/health",
        "/healthz",
        "/ping",
        "/metrics",
        "/favicon.ico",
    };

    /// <summary>
    /// Логировать тело запроса (только для loggable content-types).
    /// </summary>
    public bool LogRequestBody { get; init; } = false;
}
