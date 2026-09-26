namespace InfinityHairartsAPI.Modals;

public sealed class SalonOwnerDashboardResponse
{
    public SalonOwnerDashboardSummary Summary { get; set; } = new();
    public IReadOnlyList<SalonOwnerDashboardAppointment> UpcomingAppointments { get; set; } = [];
    public IReadOnlyList<SalonOwnerDashboardDay> WeeklyBookings { get; set; } = [];
    public IReadOnlyList<SalonOwnerDashboardServiceItem> PopularServices { get; set; } = [];
}

public sealed class SalonOwnerDashboardSummary
{
    public int TodayBookings { get; set; }
    public int TodaySeats { get; set; }
    public decimal TodayRevenue { get; set; }
    public int TotalCustomers { get; set; }
    public int ActiveServices { get; set; }
    public int UpcomingBookings { get; set; }
}

public sealed class SalonOwnerDashboardAppointment
{
    public Guid SeatBookingDetailsID { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string MobileNo { get; set; } = string.Empty;
    public DateTime BookingDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public int SeatCount { get; set; }
    public string BookingStatus { get; set; } = string.Empty;
}

public sealed class SalonOwnerDashboardDay
{
    public DateTime BookingDate { get; set; }
    public int BookingCount { get; set; }
}

public sealed class SalonOwnerDashboardServiceItem
{
    public Guid HairCutItemID { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public decimal BookingCount { get; set; }
    public decimal Revenue { get; set; }
}
