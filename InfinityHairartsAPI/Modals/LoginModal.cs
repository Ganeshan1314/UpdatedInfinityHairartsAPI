namespace InfinityHairartsAPI.Modals
{
    public class LoginModal
    {
        public class CustomerLoginRequest
        {
            public string? MobileNo { get; set; }
            public string? Address { get; set; }
        }
        public class getaddressModal
        {
            public string? MobileNo { get; set; }
        }
        public class UpdateCustomerProfileRequest
        {
            public string? FirstName { get; set; }
            public string? EmailAddress { get; set; }
            public int UserTypeID { get; set; } = 1;
            public string? ImageName { get; set; }
            public string? GeneralAddress { get; set; }
        }
    }
}
