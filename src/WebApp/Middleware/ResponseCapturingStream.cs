using System.Text;

namespace WebApp.Middleware;

/// <summary>
/// Поток-обёртка над Response.Body.
///
/// Всегда пишет данные в исходный поток (pass-through), поэтому:
/// - SSE-события уходят клиенту немедленно
/// - File stream / video / audio не буферизуются в памяти
/// - WebSocket-апгрейды обрабатываются без вмешательства
///
/// Дополнительно копирует данные во внутренний буфер, пока:
/// - буферизация не отключена через <see cref="DisableCapture"/>
/// - не превышен лимит <see cref="MaxCaptureBytes"/>
/// </summary>
internal sealed class ResponseCapturingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _buffer = new();
    private bool _capture = true;
    private bool _truncated;

    public int MaxCaptureBytes { get; }

    public ResponseCapturingStream(Stream inner, int maxCaptureBytes)
    {
        _inner = inner;
        MaxCaptureBytes = maxCaptureBytes;
    }

    /// <summary>
    /// Отключает буферизацию (вызывается для SSE / file streams).
    /// Уже записанные данные из буфера очищаются, чтобы не занимать память.
    /// </summary>
    public void DisableCapture()
    {
        _capture = false;
        _buffer.SetLength(0);
    }

    /// <summary>
    /// Возвращает захваченное тело ответа или <c>null</c> если буфер пуст / захват отключён.
    /// </summary>
    public string? GetCapturedBody()
    {
        if (_buffer.Length == 0)
            return null;

        _buffer.Seek(0, SeekOrigin.Begin);
        var text = Encoding.UTF8.GetString(_buffer.ToArray());
        return _truncated ? text + "... [обрезано]" : text;
    }

    // -------------------------------------------------------------------------
    // Write-through: сначала пишем в inner, потом в буфер
    // -------------------------------------------------------------------------

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        CaptureBytes(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        CaptureBytes(buffer);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _inner.WriteAsync(buffer, offset, count, ct);
        CaptureBytes(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        CaptureBytes(buffer.Span);
    }

    private void CaptureBytes(ReadOnlySpan<byte> data)
    {
        if (!_capture || _truncated)
            return;

        var remaining = MaxCaptureBytes - (int)_buffer.Length;
        if (remaining <= 0)
        {
            _truncated = true;
            return;
        }

        var toWrite = Math.Min(data.Length, remaining);
        _buffer.Write(data[..toWrite]);

        if (toWrite < data.Length)
            _truncated = true;
    }

    // -------------------------------------------------------------------------
    // Flush делегируем в inner — критично для SSE (flush отправляет событие)
    // -------------------------------------------------------------------------

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    // -------------------------------------------------------------------------
    // Остальные члены Stream
    // -------------------------------------------------------------------------

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin)
        => _inner.Seek(offset, origin);

    public override void SetLength(long value)
        => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _buffer.Dispose();
        base.Dispose(disposing);
    }
}
