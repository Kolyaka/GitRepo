using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using WebApp.Options;

namespace WebApp.Middleware;

/// <summary>
/// Middleware для логирования HTTP-запросов.
///
/// Стратегия логирования:
/// - HTTP 2xx/3xx  → только строка с методом, путём, статусом и временем (Info)
/// - HTTP 4xx/5xx  → то же + заголовки запроса + тело запроса + тело ответа (Warning)
/// - Исключение    → то же + exception object (Error), исключение пробрасывается
///
/// Безопасность:
/// - Чувствительные заголовки маскируются (Authorization, Cookie и др.)
/// - Тело читается только для текстовых Content-Type, с лимитом размера
/// - WebSocket-апгрейды пропускаются без оборачивания
/// - SSE / file stream / video — Response.Body не буферизуется:
///   данные уходят клиенту сразу через <see cref="ResponseCapturingStream"/>
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private const string MaskedValue = "***MASKED***";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly RequestLoggingOptions _options;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger,
        IOptions<RequestLoggingOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Исключённые пути (health, metrics и т.д.) — без логирования
        if (IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // 2. WebSocket-апгрейды не оборачиваем — протокол меняется после handshake
        if (context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // 3. Включаем буферизацию тела запроса только если нужно и только для текста.
        //    EnableBuffering() сбрасывает на диск при превышении лимита — не OOM.
        //    Тело будет прочитано только при ошибке (lazy read).
        if (_options.LogRequestBody)
            TryEnableRequestBuffering(context.Request);

        // 4. Оборачиваем Response.Body в capturing-поток.
        //    Данные всегда пишутся в оригинальный поток (pass-through),
        //    поэтому SSE и стримы работают без задержек.
        var originalBody = context.Response.Body;
        using var capturingStream = new ResponseCapturingStream(originalBody, _options.MaxResponseBodySizeBytes);
        context.Response.Body = capturingStream;

        // 5. Когда ответ начинает писаться — проверяем Content-Type.
        //    Для SSE / file / video / audio отключаем буферизацию.
        context.Response.OnStarting(() =>
        {
            if (IsNonBufferableContentType(context.Response.ContentType))
                capturingStream.DisableCapture();

            return Task.CompletedTask;
        });

        bool errorLogged = false;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            errorLogged = true;

            // Восстанавливаем тело до логирования (на случай если что-то читает его)
            context.Response.Body = originalBody;

            await LogErrorAsync(context, capturingStream, stopwatch.ElapsedMilliseconds, ex);
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;

            if (!errorLogged)
            {
                stopwatch.Stop();

                if (context.Response.StatusCode >= 400)
                    await LogErrorAsync(context, capturingStream, stopwatch.ElapsedMilliseconds);
                else
                    LogSuccess(context, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Логирование
    // -------------------------------------------------------------------------

    private void LogSuccess(HttpContext context, long elapsedMs)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        _logger.LogInformation(
            "HTTP {Method} {Path}{QueryString} → {StatusCode} ({ElapsedMs}ms)",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty,
            context.Response.StatusCode,
            elapsedMs);
    }

    private async Task LogErrorAsync(
        HttpContext context,
        ResponseCapturingStream capturingStream,
        long elapsedMs,
        Exception? exception = null)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
            return;

        var headers = MaskSensitiveHeaders(context.Request.Headers);
        var requestBody = _options.LogRequestBody
            ? await TryReadRequestBodyAsync(context.Request)
            : null;

        var responseBody = capturingStream.GetCapturedBody();

        var logLevel = exception is not null ? LogLevel.Error : LogLevel.Warning;

        _logger.Log(
            logLevel,
            exception,
            "HTTP {Method} {Path}{QueryString} → {StatusCode} ({ElapsedMs}ms) | " +
            "RequestHeaders: {@RequestHeaders} | RequestBody: {RequestBody} | ResponseBody: {ResponseBody}",
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty,
            context.Response.StatusCode,
            elapsedMs,
            headers,
            requestBody ?? "[не логируется]",
            responseBody ?? "[пустой ответ]");
    }

    // -------------------------------------------------------------------------
    // Работа с телом запроса
    // -------------------------------------------------------------------------

    private void TryEnableRequestBuffering(HttpRequest request)
    {
        if (HasLoggableContentType(request.ContentType))
            request.EnableBuffering();
    }

    private async Task<string?> TryReadRequestBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanSeek || !HasLoggableContentType(request.ContentType))
            return null;

        try
        {
            if (request.ContentLength > _options.MaxRequestBodySizeBytes)
                return $"[тело превышает лимит {_options.MaxRequestBodySizeBytes} байт]";

            request.Body.Seek(0, SeekOrigin.Begin);

            using var reader = new StreamReader(
                request.Body,
                encoding: Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var body = await reader.ReadToEndAsync();
            request.Body.Seek(0, SeekOrigin.Begin);

            return body.Length > _options.MaxRequestBodySizeBytes
                ? body[.._options.MaxRequestBodySizeBytes] + "... [обрезано]"
                : body;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось прочитать тело запроса для логирования");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Вспомогательные методы
    // -------------------------------------------------------------------------

    private Dictionary<string, string> MaskSensitiveHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            result[key] = _options.SensitiveHeaders.Contains(key) ? MaskedValue : value.ToString();
        }
        return result;
    }

    private bool HasLoggableContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return _options.LoggableRequestContentTypes.Contains(mediaType);
    }

    private bool IsNonBufferableContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        var mediaType = contentType.Split(';', 2)[0].Trim();

        if (_options.NonBufferableContentTypes.Contains(mediaType))
            return true;

        foreach (var prefix in _options.NonBufferableContentTypePrefixes)
        {
            if (mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool IsExcludedPath(PathString path)
    {
        foreach (var excluded in _options.ExcludedPaths)
        {
            if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
