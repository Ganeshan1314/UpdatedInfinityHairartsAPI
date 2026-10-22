namespace InfinityHairartsAPI.Modals;

public sealed class SalonOwnerDashboardResponse
{
    public SalonOwnerDashboardSummary Summary { get; set; } = new();
    public IReadOnlyList<SalonOwnerDashboardAppointment> UpcomingAppointments { get; set; } = [];
    public IReadOnlyList<SalonOwnerDashboardDay> WeeklyBookings { get; set; } = [];
    public IReadOnlyList<SalonOwnerDashboardServiceItem> PopularServices { get; set; } = [];
    public SalonOwnerDashboardProfile Salon { get; set; } = new();
    public IReadOnlyList<SalonOwnerDashboardCatalogueService> Services { get; set; } = [];
    public DateOnly Date { get; set; }
}

public sealed class SalonOwnerDashboardProfile
{
    public Guid SalonMasterID { get; set; }
    public string SalonName { get; set; } = string.Empty;
    public Guid LocationMasterID { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string SalonAddress { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public string? OpeningTime { get; set; }
    public string? ClosingTime { get; set; }
    public int ChairCount { get; set; }
    public int? WeeklyHoliday { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string City { get; set; } = string.Empty;
    public string Pincode { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string OwnerEmailAddress { get; set; } = string.Empty;
    public bool? IsVisibleToCustomers { get; set; }
}

public sealed class SalonOwnerDashboardCatalogueService
{
    public Guid SalonServiceID { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int DurationMinutes { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class SalonOwnerDashboardSummary
{
    public int TodayBookings { get; set; }
    public int TodaySeats { get; set; }
    public decimal TodayRevenue { get; set; }
    public int TotalCustomers { get; set; }
    public int ActiveServices { get; set; }
    public int UpcomingBookings { get; set; }
    public int CompletedBookings { get; set; }
    public int PendingBookings { get; set; }
    public int TotalBookings { get; set; }
    public int TotalPendingBookings { get; set; }
    public int TotalCompletedBookings { get; set; }
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
