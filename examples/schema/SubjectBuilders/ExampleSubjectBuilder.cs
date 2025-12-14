namespace CLOOPS.NATS.SubjectBuilders;

/// <summary>
/// Builds subjects for the cloops nats example
/// </summary>
public class ExampleSubjectBuilder
{
    private ICloopsNatsClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExampleSubjectBuilder"/> class.
    /// </summary>
    /// <param name="cnc"></param>
    public ExampleSubjectBuilder(ICloopsNatsClient cnc)
    {
        _client = cnc;
    }

    /// <summary>
    /// P_Subject (for publishing) to save person
    /// </summary>
    /// <param name="id">Person ID</param>
    /// <returns>Publishing Subject</returns>
    public P_Subject<Person> P_SavePerson(string id) =>
        new P_Subject<Person>(_client, $"test.persons.{id}.save");

    /// <summary>
    /// P_Subject (for publishing) to update person
    /// </summary>
    /// <param name="id">Person ID</param>
    /// <returns>Publishing Subject</returns>
    public S_Subject<Person> S_UpdatePerson(string id) =>
        new S_Subject<Person>(_client, $"test.persons.{id}.update");

    public R_Subject<Person, Person> echo() =>
        new R_Subject<Person, Person>(_client, "dev.echo");
}