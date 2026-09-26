namespace InfinityHairartsAPI.Modals;

public sealed class CompleteBookingQrRequest
{
    public string QrReference { get; set; } = string.Empty;
}

public sealed class CompleteBookingQrResponse
{
    public Guid SeatBookingDetailsID { get; set; }
    public Guid TimeAllocationID { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime BookingDate { get; set; }
    public TimeSpan BookingTime { get; set; }
    public DateTime CompletedUtc { get; set; }
    public bool WasAlreadyCompleted { get; set; }
}
