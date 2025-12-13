# Contributing to Events

Events are the core message structures that enable communication between services through request/reply and publish/subscribe patterns. This guide ensures consistency, maintainability, and backward compatibility across all event definitions.

## 📋 Event Design Principles

### 1. **Hierarchical Organization**

Events must be organized in a clear domain hierarchy that reflects your system architecture.

**Structure**: `Events/{Domain}/{Subdomain}/`

**Examples**:

- `Events/CP/Infra/` - Connection Loops infrastructure events
- `Events/Auth/User/` - User authentication events
- `Events/Billing/Invoice/` - Invoice-related events

**File Organization**:

- ✅ **Preferred**: One event class per file for clarity
- ✅ **Acceptable**: Related events in the same file when they're tightly coupled
- ❌ **Avoid**: Mixing unrelated events in a single file

### 2. **Comprehensive Documentation**

Every event and property must be thoroughly documented.

**Requirements**:

- Clear class-level summary explaining the event's purpose
- Detailed property documentation with data types and constraints
- Usage examples where helpful
- Business context and trigger conditions

**Example**:

```csharp
/// <summary>
/// Represents an infrastructure effect that has been triggered in the system.
/// This event is published when automated processes or user actions result
/// in infrastructure changes such as deployments, scaling, or configuration updates.
/// </summary>
public class EffectTriggered
{
    /// <summary>
    /// Unique identifier for the triggered effect.
    /// Used for tracking and correlation across distributed systems.
    /// </summary>
    public Guid EffectId { get; set; }

    /// <summary>
    /// Timestamp when the effect was triggered (UTC).
    /// Precision should be sufficient for ordering and audit trails.
    /// </summary>
    public DateTime TriggeredAt { get; set; }
}
```

## 🔄 Backward Compatibility Guidelines

### 3. **Safe Event Evolution**

When modifying existing events, maintain backward compatibility to prevent breaking changes.

**Safe Changes** ✅:

- Adding new optional properties
- Adding new enum values (append only)
- Expanding string length limits
- Adding documentation

**Breaking Changes** ❌:

- Removing properties
- Changing property types
- Renaming properties
- Making optional properties required
- Changing enum values

### 4. **Event Versioning Strategy**

When breaking changes are necessary, use explicit versioning.

**Naming Convention**: `{EventName}_V{Number}`

**Examples**:

```csharp
// Original event
public class EffectTriggered { }

// New version with breaking changes
public class EffectTriggered_V2 { }
```

**Migration Path**:

1. Create new versioned event alongside the original
2. Update publishers to use new version
3. Ensure consumers handle both versions during transition
4. Deprecate old version after full migration

## 📤 Reply Event Requirements

### 5. **Always Include Reply Events**

Every event must have a corresponding reply event, even if not immediately needed for request/reply patterns.

**Benefits**:

- Future-proofs your event design
- Enables easy migration to request/reply patterns
- Provides consistent structure across all events
- Supports audit and acknowledgment scenarios

**Naming Convention**: `{EventName}_Reply`

**Example**:

```csharp
// Primary event
public class EffectTriggered
{
    public Guid EffectId { get; set; }
    public DateTime TriggeredAt { get; set; }
}

// Corresponding reply event
public class EffectTriggered_Reply
{
    public Guid EffectId { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ProcessedAt { get; set; }
}
```

## 🔍 Code Review Checklist

When reviewing event changes, ensure:

- [ ] **Organization**: Event is in the correct domain hierarchy
- [ ] **Documentation**: All classes and properties have comprehensive comments
- [ ] **Compatibility**: Changes don't break existing consumers
- [ ] **Reply Events**: Corresponding reply event exists and follows naming convention
- [ ] **Versioning**: Breaking changes use proper versioning strategy
- [ ] **Consistency**: Follows established patterns and conventions
- [ ] **Testing**: Event serialization/deserialization works correctly

## 🎯 Best Practices

### Property Design

- Use meaningful, descriptive property names
- Prefer value types over reference types where appropriate
- Include validation attributes when beneficial
- Use UTC timestamps consistently
- Consider nullable reference types for optional properties

### Event Naming

- Use PascalCase for event names
- Choose action-based names (e.g., `OrderCreated`, `PaymentProcessed`)
- Avoid abbreviations unless they're widely understood
- Keep names concise but descriptive

### Schema Evolution

- Plan for growth - include extensibility points
- Use composition over inheritance
- Consider including schema version metadata
- Document migration paths for breaking changes
