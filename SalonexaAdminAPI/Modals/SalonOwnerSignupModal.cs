using System.ComponentModel.DataAnnotations;

namespace SalonexaAdminAPI.Modals;

/// <summary>Multipart form posted by the admin app's signup screen (same field names).</summary>
public sealed class SalonOwnerSignupRequest : IValidatableObject
{
    [Required, StringLength(150)]
    public string OwnerName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(254)]
    [RegularExpression("^(?=.{1,64}@)[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?i:gmail\\.com)$",
        ErrorMessage = "Enter a valid Gmail address ending in @gmail.com.")]
    public string EmailAddress { get; set; } = string.Empty;

    [Required, RegularExpression("^[A-Za-z0-9._-]{3,100}$",
        ErrorMessage = "Use 3–100 letters, numbers, dots, underscores or hyphens for your username.")]
    public string UserName { get; set; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string SalonName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Upload a salon photo or logo.")]
    public IFormFile? Image { get; set; }

    public Guid LocationMasterID { get; set; }

    [Required, StringLength(150)]
    public string City { get; set; } = string.Empty;

    [Required, RegularExpression("^[1-9][0-9]{5}$", ErrorMessage = "Enter a valid 6-digit PIN code.")]
    public string Pincode { get; set; } = string.Empty;

    [Range(-90, 90)]
    public double? Latitude { get; set; }

    [Range(-180, 180)]
    public double? Longitude { get; set; }

    [Required, RegularExpression("^\\+?[0-9]{7,15}$", ErrorMessage = "Enter 7–15 digits with an optional +.")]
    public string ContactNumber { get; set; } = string.Empty;

    [Required, StringLength(500)]
    public string SalonAddress { get; set; } = string.Empty;

    public bool AcceptPrivacyPolicy { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Latitude.HasValue != Longitude.HasValue
            || (Latitude.HasValue && !double.IsFinite(Latitude.Value))
            || (Longitude.HasValue && !double.IsFinite(Longitude.Value)))
            yield return new ValidationResult("Provide a valid latitude and longitude together.",
                [nameof(Latitude), nameof(Longitude)]);
        if (!AcceptPrivacyPolicy)
            yield return new ValidationResult("Please accept the privacy policy to create an account.",
                [nameof(AcceptPrivacyPolicy)]);
    }
}

/// <summary>Values returned by dbo.RegisterSalonOwner.</summary>
public enum SignupResult
{
    Created = 0,
    InvalidDetails = 1,
    InvalidLocation = 2,
    DuplicateUserName = 3,
    DuplicateSalon = 4,
    DuplicateDetails = 5,
}
