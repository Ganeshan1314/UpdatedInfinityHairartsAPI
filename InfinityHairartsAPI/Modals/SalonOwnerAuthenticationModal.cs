using System.ComponentModel.DataAnnotations;

namespace InfinityHairartsAPI.Modals;

public class SalonOwnerLoginRequest
{
    [Required]
    [MaxLength(100)]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Password { get; set; } = string.Empty;
}

public sealed class InitializeSalonOwnerRequest : SalonOwnerLoginRequest
{
    [Required]
    public Guid SalonMasterID { get; set; }

    [Required]
    [MaxLength(150)]
    public string OwnerName { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(254)]
    public string? EmailAddress { get; set; }
}

public sealed class SalonOwnerLoginResponse
{
    public Guid SalonOwnerID { get; set; }
    public Guid SalonMasterID { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string SalonName { get; set; } = string.Empty;
}
