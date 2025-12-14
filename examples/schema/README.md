# Schema-Based Message Architecture

This directory demonstrates a robust schema-based approach to building microservices with NATS. A strong, centralized schema is one of the most important foundations for building reliable microservices.

## Why Schema Matters

One of the most important things you would want to do when building microservices is have a strong schema that is central to all of your services. This will help you:

- **Avoid runtime errors** because of malformed messages/events.
- **Document your interfaces** quite easily and enforce a strong governance process on changing the interfaces.
- **Implement input validation** of messages so that your handlers **never** receive invalid messages.

## One Message Per Subject: The Key to Stability

This schema example follows a critical design principle: **one message type per subject**. This approach provides several stability benefits:

### Type Safety at Compile Time

- Each subject is strongly typed to a specific message class
- The compiler prevents you from accidentally publishing the wrong message type to a subject
- IntelliSense provides autocomplete and type checking when building subjects

### Runtime Safety

- No runtime payload parsing errors on the consumer side
- Deserialization failures are caught early with clear error messages
- Handlers can trust that the message type matches the subject

### Clear Contract Definition

- The relationship between subjects and message types is explicit and discoverable
- Subject builders enforce the correct event-to-subject mapping
- Changes to message structure are immediately visible across all services

### Example: Type-Safe Subject Construction

```csharp
// Subject builder ensures type safety
var personSaveSubject = client.Subjects().Example().P_SavePerson(person.Id);
// ✅ Can only publish Person to this subject
await personSaveSubject.Publish(person);

// ❌ Compiler error if you try to publish wrong type
// await personSaveSubject.Publish(wrongMessage); // Won't compile!
```

## Automatic Message Validation

Messages with a `Validate()` method are automatically validated before processing. Invalid messages are never sent to your handlers.

### How It Works

All messages inherit from `BaseMessage`, which provides a `Validate()` method that uses Data Annotations to validate message properties:

```csharp
public abstract class BaseMessage
{
    public void Validate()
    {
        var validationContext = new ValidationContext(this);
        var validationResults = new List<ValidationResult>();

        bool isValid = Validator.TryValidateObject(
            this, validationContext, validationResults,
            validateAllProperties: true
        );

        if (!isValid)
        {
            var errorMessages = validationResults
                .Select(r => r.ErrorMessage)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            throw new ValidationException(
                $"Message validation failed: {string.Join("; ", errorMessages)}"
            );
        }
    }
}
```

### Validation on Publish

All publishing methods (`Publish`, `StreamPublish`, `Request`) automatically validate messages before sending:

```csharp
// P_Subject - Core NATS publishing
await subject.Publish(person); // Validates before publishing

// S_Subject - JetStream publishing
await subject.StreamPublish(person); // Validates before publishing

// R_Subject - Request-Reply
await subject.Request(person); // Validates before sending
```

If validation fails, a `ValidationException` is thrown **before** the message is sent, preventing invalid data from entering your system.

### Defining Validation Rules

Use Data Annotations to define validation rules on your message classes:

```csharp
public class Person : BaseMessage
{
    [Required(ErrorMessage = "Id is required")]
    public string Id { get; set; } = "";

    [Required(ErrorMessage = "Name is required")]
    public string Name { get; set; } = "";

    [Range(0, 150, ErrorMessage = "Age must be between 0 and 150")]
    public int Age { get; set; } = 0;

    [Required(ErrorMessage = "Address is required")]
    public string Addr { get; set; } = "";
}
```

### Validation on Consumption

When messages are consumed by handlers, they are automatically validated during deserialization. If a message fails validation:

- The validation exception is caught and logged
- The message is **not** delivered to your handler
- You can configure retry or dead-letter queue behavior as needed

This ensures your handlers **never** receive invalid messages, eliminating the need for defensive validation code in every handler.

## Architecture Overview

```
Messages/
  ├── BaseMessage.cs      # Base class with Validate() method
  └── Person.cs           # Example message with validation attributes

SubjectTypes/
  ├── P_Subject.cs        # Core NATS publishing (validates on publish)
  ├── S_Subject.cs        # JetStream publishing (validates on publish)
  └── R_Subject.cs        # Request-Reply (validates on request)

SubjectBuilders/
  └── ExampleSubjectBuilder.cs  # Type-safe subject construction

Extensions/
  └── CloopsNatsClientExtension.cs  # Extension methods for subject builders
```

## Best Practices

1. **Always inherit from `BaseMessage`** - This ensures all messages have validation capabilities
2. **Use Data Annotations liberally** - Define clear validation rules for all required fields
3. **One message per subject** - Maintain type safety and clear contracts
4. **Use Subject Builders** - Never construct subjects manually; use builders for type safety
5. **Trust the validation** - Handlers can assume messages are valid; no need for defensive checks

## Example Usage

```csharp
// 1. Create a validated message
var person = new Person
{
    Id = "123",
    Name = "John Doe",
    Age = 30,
    Addr = "123 Main St"
};

// 2. Get type-safe subject from builder
var subject = client.Subjects().Example().P_SavePerson(person.Id);

// 3. Publish (automatically validates)
try
{
    await subject.Publish(person); // ✅ Valid message - publishes successfully
}
catch (ValidationException ex)
{
    // ❌ Invalid message - caught before publishing
    Console.WriteLine($"Validation failed: {ex.Message}");
}

// 4. Handler receives validated message
[NatsConsumer("test.persons.*.save")]
public Task<NatsAck> HandleSavePerson(NatsMsg<Person> msg, CancellationToken ct = default)
{
    // ✅ msg.Data is guaranteed to be valid - no need to check!
    var person = msg.Data;
    // Process the person...
    return Task.FromResult(new NatsAck(true));
}
```

## Summary

This schema architecture provides:

- ✅ **Compile-time type safety** through subject builders
- ✅ **Runtime validation** before messages enter the system
- ✅ **Handler confidence** - handlers never see invalid messages
- ✅ **Clear contracts** - one message type per subject
- ✅ **Easy documentation** - schema serves as living documentation
- ✅ **Governance** - schema changes are visible and reviewable

By following this pattern, you build microservices that are more stable, easier to maintain, and less prone to runtime errors.
