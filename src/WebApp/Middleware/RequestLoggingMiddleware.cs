using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using WebApp.Options;

namespace WebApp.Middleware;

/// <summary>
/// Middleware для логирования HTTP-запросов.
/// При ошибках (статус >= 400) дополнительно логирует тело ответа.
///
/// Меры безопасности:
/// - Маскирование чувствительных заголовков (Authorization, Cookie и др.)
/// - Ограничение размера читаемого тела (защита от OOM)
/// - Логирование тела запроса только для текстовых Content-Type
/// - Исключение системных путей (health, metrics) из логирования
/// - Перехват исключений: ошибки логирования не роняют запрос
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
        // Пропускаем исключённые пути без логирования
        if (IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestInfo = await CaptureRequestAsync(context.Request);

        // Подменяем Response.Body буфером для перехвата ответа при ошибке
        var originalResponseBody = context.Response.Body;
        using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogRequest(context, requestInfo, stopwatch.ElapsedMilliseconds, responseBuffer, ex);
            throw; // Не подавляем исключение — позволяем обработчику ошибок сработать
        }
        finally
        {
            stopwatch.Stop();

            // Копируем буфер обратно в оригинальный поток
            responseBuffer.Seek(0, SeekOrigin.Begin);
            await responseBuffer.CopyToAsync(originalResponseBody);
            context.Response.Body = originalResponseBody;

            if (context.Response.StatusCode >= 400)
            {
                LogRequest(context, requestInfo, stopwatch.ElapsedMilliseconds, responseBuffer);
            }
            else
            {
                LogRequestSuccess(context, requestInfo, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Вспомогательные методы
    // -------------------------------------------------------------------------

    private async Task<RequestInfo> CaptureRequestAsync(HttpRequest request)
    {
        var headers = MaskSensitiveHeaders(request.Headers);
        string? body = null;

        if (_options.LogRequestBody && HasLoggableContentType(request.ContentType))
        {
            body = await ReadBodyAsync(request);
        }

        return new RequestInfo(
            Method: request.Method,
            Path: request.Path,
            QueryString: request.QueryString.HasValue ? request.QueryString.Value : null,
            Headers: headers,
            Body: body
        );
    }

    /// <summary>
    /// Читает тело запроса с ограничением размера.
    /// Включает EnableBuffering, чтобы тело можно было прочитать повторно.
    /// </summary>
    private async Task<string?> ReadBodyAsync(HttpRequest request)
    {
        try
        {
            request.EnableBuffering();

            if (request.ContentLength > _options.MaxRequestBodySizeBytes)
            {
                return $"[Тело запроса превышает лимит {_options.MaxRequestBodySizeBytes} байт]";
            }

            using var reader = new StreamReader(
                request.Body,
                encoding: Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);  // НЕ закрываем — поток нужен следующему middleware

            var body = await reader.ReadToEndAsync();
            request.Body.Seek(0, SeekOrigin.Begin);

            return body.Length > _options.MaxRequestBodySizeBytes
                ? body[.._options.MaxRequestBodySizeBytes] + "... [обрезано]"
                : body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать тело запроса");
            return null;
        }
    }

    /// <summary>
    /// Читает тело ответа из буфера с ограничением размера.
    /// </summary>
    private string? ReadResponseBody(MemoryStream responseBuffer)
    {
        try
        {
            if (responseBuffer.Length == 0)
                return null;

            responseBuffer.Seek(0, SeekOrigin.Begin);
            var bytesToRead = (int)Math.Min(responseBuffer.Length, _options.MaxResponseBodySizeBytes);
            var buffer = new byte[bytesToRead];
            _ = responseBuffer.Read(buffer, 0, bytesToRead);

            var body = Encoding.UTF8.GetString(buffer);
            return responseBuffer.Length > _options.MaxResponseBodySizeBytes
                ? body + "... [обрезано]"
                : body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать тело ответа");
            return null;
        }
    }

    private Dictionary<string, string> MaskSensitiveHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in headers)
        {
            result[key] = _options.SensitiveHeaders.Contains(key)
                ? MaskedValue
                : value.ToString();
        }

        return result;
    }

    private bool HasLoggableContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        // Content-Type может содержать параметры: "application/json; charset=utf-8"
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return _options.LoggableContentTypes.Contains(mediaType);
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

    // -------------------------------------------------------------------------
    // Методы логирования — используем структурированное логирование
    // -------------------------------------------------------------------------

    private void LogRequestSuccess(HttpContext context, RequestInfo req, long elapsedMs)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        _logger.LogInformation(
            "HTTP {Method} {Path}{QueryString} → {StatusCode} ({ElapsedMs}ms)",
            req.Method,
            req.Path,
            req.QueryString ?? string.Empty,
            context.Response.StatusCode,
            elapsedMs);
    }

    private void LogRequest(
        HttpContext context,
        RequestInfo req,
        long elapsedMs,
        MemoryStream responseBuffer,
        Exception? exception = null)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
            return;

        var responseBody = ReadResponseBody(responseBuffer);

        // Структурированное логирование: не интерполируем строки вручную,
        // чтобы избежать log injection и обеспечить корректное хранение в Seq/ELK
        _logger.LogWarning(
            exception,
            "HTTP {Method} {Path}{QueryString} → {StatusCode} ({ElapsedMs}ms) | " +
            "RequestHeaders: {@RequestHeaders} | RequestBody: {RequestBody} | ResponseBody: {ResponseBody}",
            req.Method,
            req.Path,
            req.QueryString ?? string.Empty,
            context.Response.StatusCode,
            elapsedMs,
            req.Headers,
            req.Body ?? "[не логируется]",
            responseBody ?? "[пустой ответ]");
    }

    // -------------------------------------------------------------------------
    // Value object для хранения данных запроса
    // -------------------------------------------------------------------------

    private sealed record RequestInfo(
        string Method,
        string Path,
        string? QueryString,
        Dictionary<string, string> Headers,
        string? Body);
}
