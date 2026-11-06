namespace InfinityHairartsAPI.Modals;

public sealed class DirectorySalon
{
    public Guid SalonMasterID { get; set; }
    public Guid LocationMasterID { get; set; }
    public string SalonName { get; set; } = "";
    public string LocationName { get; set; } = "";
    public string SalonAddress { get; set; } = "";
    public string ContactNumber { get; set; } = "";
}

public sealed record SalonDirectoryPage(IReadOnlyList<DirectorySalon> Items, string? NextCursor);
