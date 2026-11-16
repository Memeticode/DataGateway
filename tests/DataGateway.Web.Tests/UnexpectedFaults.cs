using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DataGateway.Web.Tests;

public sealed class SyntheticFault : Exception
{
    public SyntheticFault(string message) : base(message) { }
    public SyntheticFault(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A test-only serializer fault, armed independently for each host.</summary>
public sealed class AnswerFault(Type[] roots, Func<Exception> failure) : JsonConverterFactory
{
    private int shots;
    public void Arm(int shots = 1) => Interlocked.Exchange(ref this.shots, shots);
    public void ArmAlways() => Interlocked.Exchange(ref shots, -1);
    public override bool CanConvert(Type typeToConvert) => roots.Contains(typeToConvert);
    private bool Fire()
    {
        while (true)
        {
            var current = Volatile.Read(ref shots);
            if (current == 0) return false;
            if (current < 0 || Interlocked.CompareExchange(ref shots, current - 1, current) == current) return true;
        }
    }
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var clean = new JsonSerializerOptions(options);
        clean.Converters.Remove(this);
        return (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert), this, clean)!;
    }
    private sealed class Converter<T>(AnswerFault owner, JsonSerializerOptions clean) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T>(ref reader, clean);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (owner.Fire()) throw owner.MakeFailure();
            JsonSerializer.Serialize(writer, value, clean);
        }
    }
    private Exception MakeFailure() => failure();
}

public sealed class BodyFault(Exception failure) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, run) =>
        {
            context.Request.Body = new FaultStream(failure);
            await run(context);
        });
        next(app);
    };
    private sealed class FaultStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw failure;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class ExplodingValue
{
    public string Text => throw new SyntheticFault("synthetic getter fault");
}

public sealed class ThrowingFailureProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ThrowingLogger(categoryName);
    public void Dispose() { }
    private sealed class ThrowingLogger(string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == "DataGateway.Failure") throw new InvalidOperationException("synthetic provider detail");
        }
    }
}
