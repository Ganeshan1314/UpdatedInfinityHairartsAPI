ALTER PROCEDURE [dbo].[checkcustomerIDSeatBookingDetails]
  @CustomerID uniqueidentifier
AS
BEGIN
  SELECT
    A.CustomerID,
    A.SeatBookingDetailsID,
    A.SeatCount,
    A.BookingDate
  FROM SeatBookingDetails A
  WHERE
    A.CustomerID = @CustomerID
    AND BookingStatusMasterID = 1
END
