using WebApp.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Регистрация middleware логирования.
// Настройки можно переопределить через конфигурацию или лямбду:
builder.Services.AddRequestLogging(opts =>
{
    // Логировать тело запроса (только для text/json и подобных Content-Type)
    // opts.LogRequestBody = true;

    // Уменьшить лимит при необходимости, например до 32 KB
    // opts.MaxRequestBodySizeBytes = 32 * 1024;
});

var app = builder.Build();

// ВАЖНО: UseRequestLogging размещается первым — до маршрутизации и аутентификации,
// чтобы логировать все входящие запросы, включая 401/403.
app.UseRequestLogging();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
