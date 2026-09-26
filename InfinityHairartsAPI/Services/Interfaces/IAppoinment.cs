using static InfinityHairartsAPI.Modals.AppoinmentModal;

namespace InfinityHairartsAPI.Services.Interfaces
{
    public interface IAppoinment
    {
        Tuple<List<Dictionary<string, object>>, string> getTimeAllocation();
        Tuple<List<Dictionary<string, object>>, string> getHairCut_GroupMaster();
        Tuple<string, int, List<Dictionary<string, object>>> AddtoCartHairCutItem(AddtoCartModal modal);    
        Tuple<List<Dictionary<string, object>>, string> filterHariCutItemGroupMaster(Guid HairCutItemGroupMaster_ID);
        Tuple<string, List<Dictionary<string, object>>, string> getCustomerCart();
        Tuple<string> checkCustomerSelectedSeatOrNot();
        Tuple<string, int, List<Dictionary<string, object>>> removeClientSideCartItem(AddtoCartModal modal);
        Tuple<string, int, List<Dictionary<string, object>>> getClientSideCartItemPageLoad();
        List<Dictionary<string, object>> getHairCutItemName(string HairCutItemName);
        Tuple<string, List<Dictionary<string, object>>> getSearchHairCutItemAutoComplete(Guid HairCut_Item_ID);
        Tuple<string, List<Dictionary<string, object>>> getHairCutItemListByName(string HairCut_Item_Name);
        Tuple<string, List<Dictionary<string, object>>, int, List<Dictionary<string, object>>, List<Dictionary<string, object>>> getSeatTimeAllocation();
        Tuple<string, int, List<Dictionary<string, object>>, List<string>> insertupdateSeatBookingDetails(int SeatCount, DateTime BookingDate, Guid SalonMasterID);
        Tuple<string, Guid, string> insertCustomerTimeSelection(Guid TimeAllocationID);
        Tuple<string, Guid> deleteCustomerTimeSelection(Guid TimeAllocationID);
        Tuple<string, string, int> goNextSeatSelection();
        Tuple<string, List<Dictionary<string, object>>> loadSeatBookingDetailsDatewise(DateTime selectedDate);
        Tuple<string> checkSeatCountCartItems();
        Tuple<string> clientSideCheckoutCartItem();
        Tuple<string> SendOTPCustomer();
        Tuple<string> validateCustomerOTP(string UserOTP);
        Tuple<string, string> checkCustomerOTPSentOrNot();
    }
}
