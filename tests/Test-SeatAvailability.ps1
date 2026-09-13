$ErrorActionPreference = 'Stop'

# Exercise the real availability post-processing with in-memory database rows.
# No database connection or customer booking is changed by this regression test.
$servicePath = Join-Path $PSScriptRoot '../InfinityHairartsAPI/Services/AppoinmentService.cs'
$serviceSource = Get-Content -LiteralPath $servicePath -Raw
$methodStart = $serviceSource.IndexOf('>>> getSeatTimeAllocation()')
$start = $serviceSource.IndexOf('if (DTSelectedTimeAllocation.Rows.Count == 0)', $methodStart)
$end = $serviceSource.IndexOf('list = convertDatatabletoList(DT);', $start)
if ($methodStart -lt 0 -or $start -lt 0 -or $end -le $start) {
    throw 'Cannot locate the availability post-processing in AppoinmentService.cs.'
}
$processing = $serviceSource.Substring($start, $end - $start)
# Windows PowerShell uses a C# 5 compiler. Adapt syntax only; seeded IDs are non-null.
$processing = $processing.Replace('out var selectedBookingId', 'out selectedBookingId')
$processing = $processing.Replace('out var currentSeatCount', 'out currentSeatCount')
$processing = $processing.Replace('?.ToString()', '.ToString()')

$harness = @'
#pragma warning disable 0168, 0219 // Locals also used by the pre-fix source for comparison.
using System;
using System.Data;
using System.Linq;
using System.Collections.Generic;

public static class SeatAvailabilityRegression
{
    private static DataTable convertDatatabletoList(DataTable table) { return table; }

    public static void Check(int seatCount, int bookedCount, string TodayBooking)
    {
        var DT = new DataTable();
        DT.Columns.Add("TimeAllocationID", typeof(string));
        DT.Columns.Add("EnableDisable", typeof(string));
        DT.Rows.Add("free-morning", "Enable");
        DT.Rows.Add("free-afternoon", "Enable");
        DT.Rows.Add("unavailable", "Disable");

        var DTSeatBookingDetails = new DataTable();
        DTSeatBookingDetails.Columns.Add("SeatCount", typeof(int));
        DTSeatBookingDetails.Rows.Add(seatCount);

        var DTSelectedTimeAllocation = new DataTable();
        DTSelectedTimeAllocation.Columns.Add("TimeAllocationID", typeof(string));
        DTSelectedTimeAllocation.Columns.Add("SeatBookingDetailsID", typeof(Guid));
        DTSelectedTimeAllocation.Columns.Add("BookingDate", typeof(DateTime));
        var SeatBookingDetailsID = Guid.NewGuid();
        var date = DateTime.Today.AddDays(1);
        string BookingDate = date.ToString();
        string tomorrow = BookingDate;
        string[] dateArray = new string[4];
        int SeatBookingDetailsCount = seatCount;
        Guid selectedBookingId;
        int currentSeatCount;
        DataTable listSelectedTimeAllocation = new DataTable();

        for (int i = 0; i < bookedCount; i++)
        {
            string id = "booked-" + i;
            DT.Rows.Add(id, "Disable");
            // A confirmed appointment is separate from the newly selected seat.
            DTSelectedTimeAllocation.Rows.Add(id, Guid.NewGuid(), date);
        }

        __PROCESSING__

        // Same visibility rule as the Ionic loader: disabled/booked slots hidden.
        var bookedIds = new HashSet<string>(listSelectedTimeAllocation.AsEnumerable()
            .Select(row => Convert.ToString(row["TimeAllocationID"])));
        var visible = DT.AsEnumerable()
            .Where(row => Convert.ToString(row["EnableDisable"]) != "Disable" &&
                !bookedIds.Contains(Convert.ToString(row["TimeAllocationID"])))
            .Select(row => Convert.ToString(row["TimeAllocationID"]))
            .ToArray();
        if (!visible.SequenceEqual(new[] { "free-morning", "free-afternoon" }))
        {
            throw new Exception("Free slots lost: seats=" + seatCount +
                ", confirmed bookings=" + bookedCount + ", day=" + TodayBooking +
                ", visible=" + string.Join(",", visible));
        }
        if (bookedIds.Count != bookedCount)
            throw new Exception("Existing booked slots were not preserved.");
    }
}
'@
$harness = $harness.Replace('__PROCESSING__', $processing)
Add-Type -TypeDefinition $harness -ReferencedAssemblies System.Data, System.Data.DataSetExtensions, System.Core, System.Xml -WarningAction SilentlyContinue
foreach ($dayStatus in @('Not Completed', 'Completed')) {
    foreach ($seatCount in @(1, 2)) {
        foreach ($bookedCount in @(0, 1, 2)) {
            [SeatAvailabilityRegression]::Check($seatCount, $bookedCount, $dayStatus)
            Write-Output "PASS: seats=$seatCount, confirmed bookings=$bookedCount, day=$dayStatus"
        }
    }
}
