using System.ComponentModel.DataAnnotations;

namespace SalonexaAdminAPI.Modals;

/// <summary>Salon details as shown on the admin app's "Your salon" screen (dbo.GetSalonOwnerProfile).</summary>
public sealed class OwnerSalonProfile
{
    public Guid SalonMasterID { get; set; }
    public string SalonName { get; set; } = "";
    public Guid LocationMasterID { get; set; }
    public string LocationName { get; set; } = "";
    public string SalonAddress { get; set; } = "";
    public string ContactNumber { get; set; } = "";
    public string? OpeningTime { get; set; }
    public string? ClosingTime { get; set; }
    public int ChairCount { get; set; }
    public byte? WeeklyHoliday { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string City { get; set; } = "";
    public string Pincode { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string OwnerEmailAddress { get; set; } = "";
    public bool? IsVisibleToCustomers { get; set; }
}

/// <summary>JSON body of PUT api/Admin/salon.</summary>
public sealed class OwnerSalonProfileRequest : IValidatableObject
{
    [Required, StringLength(200)]
    public string SalonName { get; set; } = "";

    public Guid LocationMasterID { get; set; }

    [Range(typeof(decimal), "-90", "90")]
    public decimal? Latitude { get; set; }

    [Range(typeof(decimal), "-180", "180")]
    public decimal? Longitude { get; set; }

    [Required, StringLength(500)]
    public string SalonAddress { get; set; } = "";

    [Required, RegularExpression("^\\+?[0-9]{7,15}$", ErrorMessage = "Enter 7–15 digits with an optional +.")]
    public string ContactNumber { get; set; } = "";

    [Required, RegularExpression("^([01]\\d|2[0-3]):[0-5]\\d$")]
    public string OpeningTime { get; set; } = "";

    [Required, RegularExpression("^([01]\\d|2[0-3]):[0-5]\\d$")]
    public string ClosingTime { get; set; } = "";

    [Range(1, 100)]
    public int ChairCount { get; set; } = 1;

    [Range(0, 6)]
    public byte? WeeklyHoliday { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Latitude.HasValue != Longitude.HasValue)
            yield return new ValidationResult("Select a valid map pin.", [nameof(Latitude), nameof(Longitude)]);
        if (LocationMasterID == Guid.Empty)
            yield return new ValidationResult("Select a location.", [nameof(LocationMasterID)]);
        if (TimeSpan.TryParse(OpeningTime, out var opens) && TimeSpan.TryParse(ClosingTime, out var closes) && closes <= opens)
            yield return new ValidationResult("Closing time must be after opening time.", [nameof(ClosingTime)]);
    }
}
