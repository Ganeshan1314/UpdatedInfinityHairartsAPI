using System.ComponentModel.DataAnnotations;

namespace InfinityHairartsAPI.Modals;

public sealed class RegisterDeviceRequest
{
    [Required]
    [StringLength(512, MinimumLength = 10)]
    public string Token { get; set; } = string.Empty;

    [StringLength(20)]
    public string Platform { get; set; } = "android";
}
