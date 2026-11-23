using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DataGateway.Web.Tests;

/// <summary>The exception tests plant to stand for a bug: a type no product code throws or catches, so a test can
/// tell that the gateway's own handling, not a lucky catch, turned it into UnexpectedError.</summary>
public sealed class SyntheticFault : Exception
{
    /// <summary>A fault with <paramref name="message"/>.</summary>
    public SyntheticFault(string message) : base(message) { }
    /// <summary>A fault with <paramref name="message"/> around <paramref name="inner"/>, for tests of how a whole
    /// exception tree is logged.</summary>
    public SyntheticFault(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A test-only serializer fault, armed independently for each host.</summary>
/// <remarks>Added to a host's JSON options, it throws while the gateway writes an answer of one of the given root
/// types, which proves a fault after the work is done still becomes the gateway's error. Disarmed, it serializes
/// normally, so the same host can then show a healthy answer.</remarks>
/// <param name="roots">The answer types whose writing fails.</param>
/// <param name="failure">Makes the exception to throw, once per shot.</param>
public sealed class AnswerFault(Type[] roots, Func<Exception> failure) : JsonConverterFactory
{
    /// <summary>Faults left to fire: 0 for none, -1 for every write.</summary>
    private int shots;
    /// <summary>Makes the next <paramref name="shots"/> writes fail.</summary>
    public void Arm(int shots = 1) => Interlocked.Exchange(ref this.shots, shots);
    /// <summary>Makes every write fail.</summary>
    public void ArmAlways() => Interlocked.Exchange(ref shots, -1);
    /// <summary>True only for the root answer types, so nested values serialize untouched.</summary>
    public override bool CanConvert(Type typeToConvert) => roots.Contains(typeToConvert);
    /// <summary>Takes one shot if any is left; true when this write must fail.</summary>
    /// <remarks>Compare-and-swap, so concurrent requests use up exactly the armed number of shots.</remarks>
    private bool Fire()
    {
        while (true)
        {
            var current = Volatile.Read(ref shots);
            if (current == 0) return false;
            if (current < 0 || Interlocked.CompareExchange(ref shots, current - 1, current) == current) return true;
        }
    }
    /// <summary>A converter for <paramref name="typeToConvert"/> that serializes with options lacking this
    /// factory.</summary>
    /// <remarks>Without removing itself, the converter would call back into itself for the same type and never
    /// end.</remarks>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var clean = new JsonSerializerOptions(options);
        clean.Converters.Remove(this);
        return (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert), this, clean)!;
    }
    /// <summary>The per-type converter: throws when the owner fires, else writes normally.</summary>
    private sealed class Converter<T>(AnswerFault owner, JsonSerializerOptions clean) : JsonConverter<T>
    {
        /// <summary>Reads normally; only writing can fail.</summary>
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T>(ref reader, clean);
        /// <summary>Throws the owner's failure when a shot fires, else writes <paramref name="value"/>.</summary>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (owner.Fire()) throw owner.MakeFailure();
            JsonSerializer.Serialize(writer, value, clean);
        }
    }
    /// <summary>Makes a fresh exception for one shot.</summary>
    private Exception MakeFailure() => failure();
}

/// <summary>A startup filter that replaces every request body with a stream whose reads throw
/// <paramref name="failure"/>.</summary>
/// <remarks>It runs ahead of the gateway's pipeline, so the gateway meets the fault exactly where it reads the body:
/// a <c>BadHttpRequestException</c> proves the known unreadable-body refusal, any other exception proves the
/// unexpected path.</remarks>
/// <param name="failure">The exception every read throws.</param>
public sealed class BodyFault(Exception failure) : IStartupFilter
{
    /// <summary>Adds the body swap before the rest of the pipeline.</summary>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, run) =>
        {
            context.Request.Body = new FaultStream(failure);
            await run(context);
        });
        next(app);
    };
    /// <summary>A readable, unseekable stream whose every read throws; length, seeking and writing are unsupported.</summary>
    private sealed class FaultStream(Exception failure) : Stream
    {
        /// <summary>True, so the body looks readable.</summary>
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

/// <summary>A row value whose only property throws when read, so a test can fail an answer after its first bytes
/// have been sent.</summary>
public sealed class ExplodingValue
{
    /// <summary>Throws a <see cref="SyntheticFault"/>.</summary>
    public string Text => throw new SyntheticFault("synthetic getter fault");
}

/// <summary>A logging provider whose loggers throw when the gateway writes a failure line, and accept everything
/// else.</summary>
/// <remarks>Proves that a broken log sink cannot change the caller's answer, and that the gateway then writes one
/// short notice to standard error instead.</remarks>
public sealed class ThrowingFailureProvider : ILoggerProvider
{
    /// <summary>A logger for <paramref name="categoryName"/>.</summary>
    public ILogger CreateLogger(string categoryName) => new ThrowingLogger(categoryName);
    /// <summary>Nothing to release.</summary>
    public void Dispose() { }
    /// <summary>Throws on any entry in the <c>DataGateway.Failure</c> category.</summary>
    private sealed class ThrowingLogger(string category) : ILogger
    {
        /// <summary>Always enabled, so the gateway really writes.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>No scopes.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Throws for the failure category; drops the rest.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == "DataGateway.Failure") throw new InvalidOperationException("synthetic provider detail");
        }
    }
}
