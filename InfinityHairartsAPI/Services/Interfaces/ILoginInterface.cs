using static InfinityHairartsAPI.Modals.LoginModal;

namespace InfinityHairartsAPI.Services.Interfaces
{
    public interface ILoginInterface
    {
        Tuple<string> CheckUserLogin(string MobileNo);
        Tuple<string, List<Dictionary<string, object>>> checkCustomerMobileNumberExist(getaddressModal modal);
        Tuple<List<Dictionary<string, object>>, string> getEmployeeDetails();
        Task<Tuple<string, List<Dictionary<string, object>>>> CustomerLoginAsync(CustomerLoginRequest modal, CancellationToken cancellationToken = default);
        
    }
}
