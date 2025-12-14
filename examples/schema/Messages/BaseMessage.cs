using System.ComponentModel.DataAnnotations;

namespace CLOOPS.NATS.Messages;

/// <summary>
/// Base class for all NATS messages.
/// Provides validation capabilities using Data Annotations.
/// All message types should inherit from this class.
/// </summary>
public abstract class BaseMessage
{
    /// <summary>
    /// Validates the message using Data Annotations.
    /// Throws <see cref="ValidationException"/> if validation fails.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when validation fails.</exception>
    public void Validate()
    {
        var validationContext = new ValidationContext(this);
        var validationResults = new List<ValidationResult>();

        bool isValid = Validator.TryValidateObject(this, validationContext, validationResults, validateAllProperties: true);

        if (!isValid)
        {
            var errorMessages = validationResults
                .Select(r => r.ErrorMessage)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            var errorMessage = string.Join("; ", errorMessages);
            throw new ValidationException($"Message validation failed: {errorMessage}");
        }
    }
}
