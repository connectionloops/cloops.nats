using System.Reflection;
using System.Threading.Channels;
using CLOOPS.NATS;
using CLOOPS.NATS.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for <see cref="CloopsNatsClientOptions"/>: the defaults must stay byte-for-byte what the client
/// always produced (the options type is purely additive), and every option must land on the connection.
/// No test connects; <see cref="NatsConnection"/> only dials on <c>ConnectAsync</c>.
/// </summary>
public class CloopsNatsClientOptionsTests
{
    private const string Url = "nats://127.0.0.1:14222";
    private const string Name = "cloops.nats.sdk.Tests";

    #region defaults

    [Fact]
    public async Task ThreeArgumentConstructor_PreservesPreviousNatsOpts()
    {
        await using var client = new CloopsNatsClient(url: Url, name: Name);

        AssertLegacyDefaults(client.Connection.Opts);
        Assert.Same(CloopsNatsClientOptions.Default, client.Options);
    }

    [Fact]
    public async Task NullOptions_PreservesPreviousNatsOpts()
    {
        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: null);

        AssertLegacyDefaults(client.Connection.Opts);
        Assert.Same(CloopsNatsClientOptions.Default, client.Options);
    }

    [Fact]
    public async Task EmptyOptions_PreservesPreviousNatsOpts()
    {
        var options = new CloopsNatsClientOptions();

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        AssertLegacyDefaults(client.Connection.Opts);
        Assert.Same(options, client.Options);
    }

    [Fact]
    public void DefaultOptions_HaveEveryPropertyUnset()
    {
        var options = CloopsNatsClientOptions.Default;

        Assert.Null(options.RequestTimeout);
        Assert.Null(options.CommandTimeout);
        Assert.Null(options.ConnectTimeout);
        Assert.Null(options.LoggerFactory);
        Assert.Null(options.Configure);
        Assert.True(options.LogConnectionEvents);
    }

    private static void AssertLegacyDefaults(NatsOpts opts)
    {
        // These are the NATS.Net defaults the client has always run with. If NATS.Net changes a default
        // this test is meant to go red: a silent change here is exactly what the options type guards against.
        Assert.Equal(TimeSpan.FromSeconds(5), opts.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), opts.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), opts.ConnectTimeout);
        Assert.Same(NullLoggerFactory.Instance, opts.LoggerFactory);

        // And the values the client has always set explicitly.
        Assert.Equal(Name, opts.Name);
        Assert.Equal(Url, opts.Url);
        Assert.Equal(BoundedChannelFullMode.Wait, opts.SubPendingChannelFullMode);
        Assert.IsType<CloopsSerializerRegistry>(opts.SerializerRegistry);
        Assert.Null(opts.AuthOpts.Creds);
    }

    #endregion defaults

    #region options applied

    [Fact]
    public async Task Timeouts_AreAppliedToTheConnection()
    {
        var options = new CloopsNatsClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            CommandTimeout = TimeSpan.FromSeconds(20),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);
        var opts = client.Connection.Opts;

        Assert.Equal(TimeSpan.FromSeconds(30), opts.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), opts.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), opts.ConnectTimeout);

        // Everything not set keeps its previous value.
        Assert.Equal(Name, opts.Name);
        Assert.Equal(Url, opts.Url);
        Assert.Equal(BoundedChannelFullMode.Wait, opts.SubPendingChannelFullMode);
        Assert.Same(NullLoggerFactory.Instance, opts.LoggerFactory);
    }

    [Fact]
    public async Task SettingOnlyRequestTimeout_LeavesOtherTimeoutsAtDefault()
    {
        var options = new CloopsNatsClientOptions { RequestTimeout = TimeSpan.FromSeconds(15) };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);
        var opts = client.Connection.Opts;

        Assert.Equal(TimeSpan.FromSeconds(15), opts.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), opts.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), opts.ConnectTimeout);
    }

    [Fact]
    public async Task InfiniteTimeout_IsAccepted()
    {
        var options = new CloopsNatsClientOptions { RequestTimeout = Timeout.InfiniteTimeSpan };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Connection.Opts.RequestTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrNegativeTimeout_IsRejected(int seconds)
    {
        var requestTimeout = new CloopsNatsClientOptions { RequestTimeout = TimeSpan.FromSeconds(seconds) };
        var commandTimeout = new CloopsNatsClientOptions { CommandTimeout = TimeSpan.FromSeconds(seconds) };
        var connectTimeout = new CloopsNatsClientOptions { ConnectTimeout = TimeSpan.FromSeconds(seconds) };

        var ex1 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, requestTimeout));
        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, commandTimeout));
        var ex3 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, connectTimeout));

        Assert.Contains(nameof(CloopsNatsClientOptions.RequestTimeout), ex1.ParamName);
        Assert.Contains(nameof(CloopsNatsClientOptions.CommandTimeout), ex2.ParamName);
        Assert.Contains(nameof(CloopsNatsClientOptions.ConnectTimeout), ex3.ParamName);
    }

    [Fact]
    public void TimeoutAboveIntMaxValueMilliseconds_IsRejected()
    {
        var tooLong = TimeSpan.FromMilliseconds((double)int.MaxValue + 1);
        var requestTimeout = new CloopsNatsClientOptions { RequestTimeout = tooLong };
        var commandTimeout = new CloopsNatsClientOptions { CommandTimeout = tooLong };
        var connectTimeout = new CloopsNatsClientOptions { ConnectTimeout = tooLong };

        var ex1 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, requestTimeout));
        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, commandTimeout));
        var ex3 = Assert.Throws<ArgumentOutOfRangeException>(() => new CloopsNatsClient(Url, Name, null, connectTimeout));

        Assert.Contains(nameof(CloopsNatsClientOptions.RequestTimeout), ex1.ParamName);
        Assert.Contains(nameof(CloopsNatsClientOptions.CommandTimeout), ex2.ParamName);
        Assert.Contains(nameof(CloopsNatsClientOptions.ConnectTimeout), ex3.ParamName);
    }

    [Fact]
    public async Task TimeoutOfExactlyIntMaxValueMilliseconds_AndInfinite_AreAccepted()
    {
        var options = new CloopsNatsClientOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(int.MaxValue),
            CommandTimeout = Timeout.InfiniteTimeSpan,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
        };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);
        var opts = client.Connection.Opts;

        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), opts.RequestTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, opts.CommandTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, opts.ConnectTimeout);
    }

    [Fact]
    public async Task RequestTimeout_PropagatesToTheJetStreamContext()
    {
        var options = new CloopsNatsClientOptions { RequestTimeout = TimeSpan.FromSeconds(30) };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        // JetStream publish acks wait on the connection's RequestTimeout; the context must share it.
        Assert.Same(client.Connection, client.JsContext.Connection);
        Assert.Equal(TimeSpan.FromSeconds(30), client.JsContext.Connection.Opts.RequestTimeout);
    }

    [Fact]
    public async Task Creds_AreStillPassedThrough_WithOptions()
    {
        const string creds = "-----BEGIN NATS USER JWT-----\nnot.a.real.jwt\n------END NATS USER JWT------\n";

        await using var client = new CloopsNatsClient(Url, Name, creds, new CloopsNatsClientOptions { RequestTimeout = TimeSpan.FromSeconds(30) });

        Assert.Equal(creds, client.Connection.Opts.AuthOpts.Creds);
    }

    #endregion options applied

    #region logger factory

    [Fact]
    public async Task LoggerFactory_IsHandedToNatsNet()
    {
        using var loggerFactory = new RecordingLoggerFactory();
        var options = new CloopsNatsClientOptions { LoggerFactory = loggerFactory };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        Assert.Same(loggerFactory, client.Connection.Opts.LoggerFactory);
    }

    [Fact]
    public async Task LoggerFactory_SubscribesConnectionEventLogging_UnderTheClientCategory()
    {
        using var loggerFactory = new RecordingLoggerFactory();
        var options = new CloopsNatsClientOptions { LoggerFactory = loggerFactory };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        // The event handlers are created eagerly so a disconnect is never missed; the only observable
        // side effect without a server is the logger category being requested.
        Assert.Contains(typeof(CloopsNatsClient).FullName!, loggerFactory.Categories);
    }

    [Fact]
    public async Task LogConnectionEvents_False_DoesNotCreateTheClientLogger()
    {
        using var loggerFactory = new RecordingLoggerFactory();
        var options = new CloopsNatsClientOptions { LoggerFactory = loggerFactory, LogConnectionEvents = false };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        Assert.Same(loggerFactory, client.Connection.Opts.LoggerFactory);
        Assert.DoesNotContain(typeof(CloopsNatsClient).FullName!, loggerFactory.Categories);
    }

    [Fact]
    public async Task LoggerFactory_SetThroughConfigure_SubscribesConnectionEventLogging()
    {
        using var loggerFactory = new RecordingLoggerFactory();
        var options = new CloopsNatsClientOptions { Configure = o => o with { LoggerFactory = loggerFactory } };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        Assert.Same(loggerFactory, client.Connection.Opts.LoggerFactory);
        Assert.Contains(typeof(CloopsNatsClient).FullName!, loggerFactory.Categories);
    }

    [Fact]
    public async Task ConnectionEvent_IsLogged_UnderTheClientCategory()
    {
        using var loggerFactory = new CapturingLoggerFactory();
        var options = new CloopsNatsClientOptions { LoggerFactory = loggerFactory };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);
        await RaiseConnectionEventAsync(client, "ConnectionDisconnected", new NatsEventArgs("socket closed"));

        var entry = Assert.Single(loggerFactory.Entries, e => e.Category == typeof(CloopsNatsClient).FullName);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("disconnected", entry.Message);
        Assert.Contains("socket closed", entry.Message);
    }

    [Theory]
    [InlineData("ConnectionOpened")]
    [InlineData("ConnectionDisconnected")]
    [InlineData("ReconnectFailed")]
    public async Task ThrowingLogger_DoesNotEscapeTheConnectionEventHandler(string eventName)
    {
        using var loggerFactory = new ThrowingLoggerFactory();
        var options = new CloopsNatsClientOptions { LoggerFactory = loggerFactory };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        // An exception escaping here would end NATS.Net's event pump for the life of the connection.
        var ex = await Record.ExceptionAsync(() => RaiseConnectionEventAsync(client, eventName, new NatsEventArgs("boom")));

        Assert.Null(ex);
        Assert.True(loggerFactory.LogCalls > 0);
    }

    /// <summary>
    /// Raises a connection event the way NATS.Net's event pump does, by invoking the event's backing
    /// delegate. Avoids needing a live server to produce a real lifecycle event.
    /// </summary>
    private static async Task RaiseConnectionEventAsync(CloopsNatsClient client, string eventName, NatsEventArgs args)
    {
        var field = typeof(NatsConnection).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var handler = field!.GetValue(client.Connection) as Delegate;
        Assert.NotNull(handler);

        foreach (var single in handler!.GetInvocationList())
        {
            await (ValueTask)single.DynamicInvoke(client.Connection, args)!;
        }
    }

    #endregion logger factory

    #region configure hook

    [Fact]
    public async Task Configure_RunsLast_AndCanOverrideEverything()
    {
        var seen = default(NatsOpts);
        var options = new CloopsNatsClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            Configure = o =>
            {
                seen = o;
                return o with { RequestTimeout = TimeSpan.FromSeconds(42), MaxReconnectRetry = 7 };
            },
        };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);
        var opts = client.Connection.Opts;

        // The hook saw the fully built options...
        Assert.NotNull(seen);
        Assert.Equal(TimeSpan.FromSeconds(30), seen!.RequestTimeout);
        Assert.Equal(Name, seen.Name);
        Assert.Equal(Url, seen.Url);
        Assert.Equal(BoundedChannelFullMode.Wait, seen.SubPendingChannelFullMode);

        // ...and its return value is what the connection runs with.
        Assert.Equal(TimeSpan.FromSeconds(42), opts.RequestTimeout);
        Assert.Equal(7, opts.MaxReconnectRetry);
        Assert.Equal(Name, opts.Name);
    }

    [Fact]
    public async Task Configure_ReturningNull_KeepsTheBuiltOptions()
    {
        var options = new CloopsNatsClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            Configure = _ => null,
        };

        await using var client = new CloopsNatsClient(Url, Name, creds: null, options: options);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Connection.Opts.RequestTimeout);
        Assert.Equal(Name, client.Connection.Opts.Name);
    }

    #endregion configure hook

    #region helpers

    /// <summary>Records which logger categories were requested; the loggers themselves discard everything.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<string> Categories { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Captures every log entry with its category, level and formatted message.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory factory, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (factory.Entries)
                {
                    factory.Entries.Add((category, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    /// <summary>
    /// Hands out loggers that throw on every <c>Log</c> call, like a broken or disposed provider. Only the
    /// client's own category throws: NATS.Net logs through the same factory while constructing the connection,
    /// and the test is about the client's event handlers, not NATS.Net's internals.
    /// </summary>
    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        private int _logCalls;

        public int LogCalls => Volatile.Read(ref _logCalls);

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(CloopsNatsClient).FullName ? new ThrowingLogger(this) : NullLogger.Instance;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger(ThrowingLoggerFactory factory) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Interlocked.Increment(ref factory._logCalls);
                throw new InvalidOperationException("logger provider is broken");
            }
        }
    }

    #endregion helpers
}
