ALTER PROCEDURE [dbo].[getCustomerBookingInformation]
  @CustomerID uniqueidentifier
AS
BEGIN
  SET NOCOUNT ON;

  SELECT
    BM.UniqueBillID,
    BM.SeatBookingDetailsID,
    CT.TimeAllocationID,
    TA.FullTiming,
    SB.BookingDate,
    SB.SeatCount,
    Completion.CompletedUtc,
    ISNULL((
      SELECT SUM(A.HairCut_Item_Total)
      FROM HairCut_Item_Customer_Bill A
      WHERE A.UniqueBillID = BM.UniqueBillID
    ), 0) AS TotalAmount,
    CR.MobileNo,
    CR.GeneralAddress,
    SUBSTRING((
      SELECT ',' + HL.HairCut_Item_Name AS 'data()'
      FROM HairCut_Item_Customer_Bill A
      INNER JOIN HairCut_Item_List HL ON HL.HairCut_Item_ID = A.HairCut_Item_ID
      WHERE A.UniqueBillID = BM.UniqueBillID
      FOR XML PATH('')
    ), 2, 9999) AS ItemName
  FROM HairCut_Item_Bill_Master BM
  INNER JOIN SeatBookingDetails SB ON SB.SeatBookingDetailsID = BM.SeatBookingDetailsID
  INNER JOIN CustomerRegistration CR ON CR.CustomerID = SB.CustomerID
  LEFT JOIN CustomerTimeSelection CT ON CT.SeatBookingDetailsID = BM.SeatBookingDetailsID
  LEFT JOIN TimeAllocation TA ON TA.TimeAllocationID = CT.TimeAllocationID
  LEFT JOIN BookingQrCompletion Completion
    ON Completion.SeatBookingDetailsID = BM.SeatBookingDetailsID
    AND Completion.TimeAllocationID = CT.TimeAllocationID
  WHERE SB.CustomerID = @CustomerID
  ORDER BY SB.BookingDate DESC, BM.UniqueBillID DESC;
END
