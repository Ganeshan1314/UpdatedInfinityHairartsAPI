CREATE OR ALTER PROCEDURE [dbo].[UpdateCustomerRegistration]
  @CustomerID uniqueidentifier,
  @FirstName nvarchar(200),
  @EmailAddress nvarchar(200),
  @UserTypeID int,
  @ImageName nvarchar(MAX),
  @GeneralAddress nvarchar(MAX)
AS
BEGIN
  SET NOCOUNT ON;

  UPDATE CR
  SET
    CR.UserName = ISNULL(@FirstName, CR.UserName),
    CR.EmailID = ISNULL(@EmailAddress, CR.EmailID),
    CR.UserTypeID = ISNULL(@UserTypeID, CR.UserTypeID),
    CR.ImageName = ISNULL(@ImageName, CR.ImageName),
    CR.GeneralAddress = ISNULL(@GeneralAddress, CR.GeneralAddress)
  FROM CustomerRegistration CR
  WHERE CR.CustomerID = @CustomerID;
END
GO
