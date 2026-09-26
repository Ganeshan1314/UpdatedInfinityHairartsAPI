namespace InfinityHairartsAPI.Modals;

public sealed class SalonMasterModal
{
    public Guid SalonMasterID { get; set; }
    public Guid LocationMasterID { get; set; }
    public string SalonName { get; set; } = string.Empty;
    public string SalonAddress { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
}
