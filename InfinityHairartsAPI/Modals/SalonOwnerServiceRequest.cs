using System.ComponentModel.DataAnnotations;

namespace InfinityHairartsAPI.Modals;

public sealed class SalonOwnerServiceRequest
{
    public const int FixedDurationMinutes = 30;
    [Required, StringLength(150)]
    public string ServiceName { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Category { get; set; } = string.Empty;

    [Required, Range(typeof(decimal), "0", "99999999.99")]
    public decimal? Price { get; set; }

    [Required, Range(FixedDurationMinutes, FixedDurationMinutes, ErrorMessage = "Service duration is fixed at 30 minutes.")]
    public int? DurationMinutes { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    [Required]
    public bool? IsActive { get; set; }

    public IFormFile? Image { get; set; }
}
