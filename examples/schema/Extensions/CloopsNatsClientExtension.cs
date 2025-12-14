using CLOOPS.NATS.SubjectBuilders;

namespace CLOOPS.NATS.Extensions;

/// <summary>
/// Contains extension methods for ICloopsNatsClient
/// </summary>
public static class CloopsNatsClientExtensions
{
    /// <summary>
    /// Gets the subject builders for the NATS client
    /// </summary>
    /// <param name="client">The NATS client</param>
    /// <returns>A SubjectBuilders instance</returns>
    public static TopLevelSubjectBuilders Subjects(this ICloopsNatsClient client)
    {
        return new TopLevelSubjectBuilders(client);
    }
}

/// <summary>
/// Provides access to subject builders for different services
/// </summary>
public class TopLevelSubjectBuilders
{
    private readonly ICloopsNatsClient _client;

    internal TopLevelSubjectBuilders(ICloopsNatsClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Gets the CLJPS (Connection Loops Job Processing Service) subject builder
    /// </summary>
    /// <returns>A CLJPS Builder instance</returns>
    public ExampleSubjectBuilder Example()
    {
        return new ExampleSubjectBuilder(_client);
    }
}
