using System.ComponentModel.DataAnnotations;
using CLOOPS.NATS.Messages;

public class Person : BaseMessage
{
    [Required(ErrorMessage = "Id is required")]
    public string Id { get; set; } = "";

    [Required(ErrorMessage = "Name is required")]
    public string Name { get; set; } = "";

    [Range(0, 150, ErrorMessage = "Age must be between 0 and 150")]
    public int Age { get; set; } = 0;

    [Required(ErrorMessage = "Name is required")]
    public string Addr { get; set; } = "";
}