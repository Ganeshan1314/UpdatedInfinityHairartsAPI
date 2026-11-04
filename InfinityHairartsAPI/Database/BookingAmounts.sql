-- Keep admin amounts aligned with the customer booking history's recorded service totals.
-- Some legacy bill headers leave BillIDTotalAmount at zero even when service lines are priced.
IF OBJECT_ID('dbo.BookingBillAmounts','V') IS NULL
    EXEC('CREATE VIEW dbo.BookingBillAmounts AS SELECT CAST(NULL AS uniqueidentifier) SeatBookingDetailsID, CAST(NULL AS decimal(38,2)) BillAmount WHERE 1=0');
GO
ALTER VIEW dbo.BookingBillAmounts
AS
    SELECT bill.SeatBookingDetailsID,
        SUM(CASE WHEN items.LineCount>0 THEN COALESCE(items.LineTotal,0) ELSE bill.BillIDTotalAmount END) BillAmount
    FROM dbo.HairCut_Item_Bill_Master bill
    OUTER APPLY (
        SELECT COUNT_BIG(*) LineCount,
            SUM(CASE WHEN COALESCE(item.IsDeleted,0)=0 THEN item.HairCut_Item_Total ELSE 0 END) LineTotal
        FROM dbo.HairCut_Item_Customer_Bill item WHERE item.UniqueBillID=bill.UniqueBillID
    ) items
    WHERE COALESCE(bill.IsDeleted,0)=0
    GROUP BY bill.SeatBookingDetailsID;
GO
