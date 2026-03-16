using WebApp.Middleware;
using WebApp.Options;

namespace WebApp.Extensions;

/// <summary>
/// Extension-методы для регистрации middleware логирования запросов.
/// </summary>
public static class RequestLoggingMiddlewareExtensions
{
    /// <summary>
    /// Добавляет <see cref="RequestLoggingMiddleware"/> в конвейер запросов.
    /// </summary>
    /// <remarks>
    /// Рекомендуется размещать как можно раньше в конвейере — до UseRouting,
    /// UseAuthentication, UseAuthorization — чтобы охватить все запросы.
    /// </remarks>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
        => app.UseMiddleware<RequestLoggingMiddleware>();

    /// <summary>
    /// Регистрирует зависимости <see cref="RequestLoggingMiddleware"/> в DI-контейнере.
    /// </summary>
    public static IServiceCollection AddRequestLogging(
        this IServiceCollection services,
        Action<RequestLoggingOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<RequestLoggingOptions>();

        if (configure is not null)
            optionsBuilder.Configure(configure);

        // Валидация при старте приложения — падаем быстро, если конфигурация некорректна
        optionsBuilder.Validate(
            opts => opts.MaxRequestBodySizeBytes > 0,
            $"{nameof(RequestLoggingOptions.MaxRequestBodySizeBytes)} должен быть больше нуля.");

        optionsBuilder.Validate(
            opts => opts.MaxResponseBodySizeBytes > 0,
            $"{nameof(RequestLoggingOptions.MaxResponseBodySizeBytes)} должен быть больше нуля.");

        optionsBuilder.ValidateOnStart();

        return services;
    }
}
