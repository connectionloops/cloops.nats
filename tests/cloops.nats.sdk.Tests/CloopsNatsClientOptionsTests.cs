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
            Configure = _ => null!,
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

    #endregion helpers
}
