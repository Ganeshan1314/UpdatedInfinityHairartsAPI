using System;
using System.Collections.Generic;

namespace InfinityHairartsAPI.Services;

public sealed record ReservationDraft(Guid SeatBookingDetailsID, string BookingDate, Guid? SalonMasterID, long Version, bool Confirmed, IReadOnlyList<ReservedTime> Selections);
