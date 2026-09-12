using static InfinityHairartsAPI.Modals.LoginModal;

namespace InfinityHairartsAPI.Services.Interfaces
{
    public interface ILoginInterface
    {
        Tuple<string> CheckUserLogin(string MobileNo);
        Tuple<string, List<Dictionary<string, object>>> checkCustomerMobileNumberExist(getaddressModal modal);
        Tuple<List<Dictionary<string, object>>, string> getEmployeeDetails();
        Tuple<string, List<Dictionary<string, object>>> customerLoginButtomClick(CustomerLoginRequest modal);
        
    }
}
