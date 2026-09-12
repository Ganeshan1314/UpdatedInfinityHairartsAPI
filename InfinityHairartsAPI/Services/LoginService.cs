using CorePush.Firebase;
using CorePush.Interfaces;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Net.Http;
using Twilio;
using Twilio.Http;
using Twilio.Rest.Lookups.V2;
using static InfinityHairartsAPI.Modals.LoginModal;
using HttpClient = System.Net.Http.HttpClient;

namespace InfinityHairartsAPI.Services
{
    public class LoginService
    {
        SqlConnection con;
        List<Dictionary<string, object>> listUserRegistration = new List<Dictionary<string, object>>();
        Dictionary<string, object> dictUserRegistration = new Dictionary<string, object>();
        DataTable DTUserRegistration = new DataTable();
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly FirebaseSender _firebaseSender;
        public LoginService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration, IWebHostEnvironment environment)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _environment = environment;

            // Temporary: do nothing here
        }
        public SqlConnection DBConnection()
        {
            con = new SqlConnection(_configuration.GetConnectionString("dbconnection"));
            con.Open();
            return con;
        }
        public string insertUserRegistration(string MobileNo)
        {
            string Message = string.Empty;
            try
            {
                Guid CustomerID = Guid.NewGuid();
                _httpContextAccessor.HttpContext?.Session.SetString("CustomerID", CustomerID.ToString());
                DataSet DS = new DataSet();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("@CustomerID", CustomerID);
                    Parameter.Add("@MobileNo", MobileNo);
                    con.Execute("insertCustomerRegistration", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    Message = "Success";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Success";
            }
            return Message;
        }
        
        public Tuple<string> CheckUserLogin(string MobileNo)
        {
            string Message = string.Empty;
            try
            {
                Message = insertUserRegistration(MobileNo);
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message);
        }
        public Tuple<string, List<Dictionary<string, object>>> checkCustomerMobileNumberExist(getaddressModal modal)
        {
            string Message = string.Empty;
            string MobileNo = modal?.MobileNo?.Trim() ?? string.Empty;
            DataTable DT = new DataTable();
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("MobileNo", MobileNo);
                    var Reader = con.ExecuteReader(
                        "SELECT FirstName, EmailAddress, GeneralAddress, ImageName, MobileNo, CustomerID FROM CustomerRegistration WHERE MobileNo = @MobileNo",
                        Parameter,
                        commandType: CommandType.Text,
                        commandTimeout: 0);
                    DT.Load(Reader);
                    if (DT.Rows.Count > 0)
                    {
                        list = convertDatatableIntoList(DT);
                    }
                    Message = "Success";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message, list);
        }

        

        public Tuple<string, List<Dictionary<string, object>>> customerLoginButtomClick(CustomerLoginRequest modal)
        {
            string Message = string.Empty;
            DataTable DT = new DataTable();
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            string MobileNumberValidation = string.Empty;
            Guid CustomerID = Guid.NewGuid();
            string MobileNo = modal?.MobileNo?.Trim() ?? string.Empty;
            string GeneralAddress = modal?.Address?.Trim() ?? string.Empty;
            //await SendPushAsync("cnANRg9CQuOmvOZD0ckE65:APA91bFsFrYolfP9KDIDqwQjqcBxuj6uVAJJ113TP4ViTdemrVui-Z-xoiTgl5fGmqh-1P0l8hKYHZPXdHloDdZqjd1bLxWWAbadZgFKic8bslPr6TnXblo");
            try
            {
                
                if (MobileNo.Length == 10)
                {
                    //MobileNumberValidation = checkValidMobileNumber(MobileNo);
                    MobileNumberValidation = "Valid";
                    if (MobileNumberValidation == "Valid")
                    {

                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("MobileNo", MobileNo);
                            var Reader = con.ExecuteReader("checkCustomerMobileNumberExist", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            DT.Load(Reader);
                            if (DT.Rows.Count > 0)
                            {
                                //CustomerID = new Guid(Convert.ToString(DT.Rows[0]["CustomerID"]).Trim());
                                CustomerID = DT.Rows[0].Field<Guid>("CustomerID");
                            }
                        }
                        if (DT.Rows.Count == 0)
                        {
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("CustomerID", CustomerID);
                                Parameter.Add("MobileNo", MobileNo);
                                Parameter.Add("GeneralAddress", GeneralAddress);
                                con.Execute("customerLoginButtomClick", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                                Message = "Success";
                                _httpContextAccessor.HttpContext?.Session.SetString("CustomerID", CustomerID.ToString());
                                //HttpContext.Current.Session["CustomerID"] = CustomerID;
                            }

                        }
                        else
                        {
                            updateCustomerNameLocation(GeneralAddress, CustomerID);
                            Message = "Success";
                            _httpContextAccessor.HttpContext?.Session.SetString("CustomerID", CustomerID.ToString());
                            //HttpContext.Current.Session["CustomerID"] = CustomerID;
                            
                        }
                    }
                    else
                    {
                        Message = "Not Valid MobileNo";
                        //HttpContext.Current.Session["CustomerID"] = CustomerID;
                        _httpContextAccessor.HttpContext?.Session.SetString("CustomerID", CustomerID.ToString());
                        list = new List<Dictionary<string, object>>();
                    }
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                list = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(Message, list);

        }
        
        public string updateCustomerNameLocation(string GeneralAddress, Guid CustomerID)
        {
            string Message = string.Empty;
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    Parameter.Add("GeneralAddress", GeneralAddress);
                    con.Execute("updateCustomerNameLocation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    Message = "Success";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Message;
        }

        public async Task<string> uploadCustomerImage(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return string.Empty;
                }

                var customerId = _httpContextAccessor.HttpContext?.Session.GetString("CustomerID");
                if (string.IsNullOrWhiteSpace(customerId))
                {
                    return string.Empty;
                }

                var extension = Path.GetExtension(file.FileName);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }

                var fileName = $"{customerId}{extension}";
                var uploadFolder = Path.Combine(_environment.ContentRootPath, "CustomerImages");
                Directory.CreateDirectory(uploadFolder);

                foreach (var existingFile in Directory.GetFiles(uploadFolder, $"{customerId}.*"))
                {
                    System.IO.File.Delete(existingFile);
                }

                var fullPath = Path.Combine(uploadFolder, fileName);

                await using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                return fileName;
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                return string.Empty;
            }
        }

        public string updateCustomerRegistration(UpdateCustomerProfileRequest modal)
        {
            string Message = string.Empty;
            try
            {
                var customerId = _httpContextAccessor.HttpContext?.Session.GetString("CustomerID");
                if (string.IsNullOrWhiteSpace(customerId))
                {
                    return "Customer session not found";
                }

                var customerGuid = Guid.Parse(customerId);
                var firstName = modal?.FirstName?.Trim() ?? string.Empty;
                var email = modal?.EmailAddress?.Trim() ?? string.Empty;
                var userTypeId = modal?.UserTypeID ?? 1;
                var imageName = modal?.ImageName?.Trim() ?? string.Empty;
                var generalAddress = modal?.GeneralAddress?.Trim() ?? string.Empty;

                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", customerGuid);
                    Parameter.Add("FirstName", firstName);
                    Parameter.Add("EmailAddress", email);
                    Parameter.Add("UserTypeID", userTypeId);
                    Parameter.Add("ImageName", imageName);
                    Parameter.Add("GeneralAddress", generalAddress);
                    con.Execute("UpdateCustomerRegistration", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    Message = "Success";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Message;
        }
        
        public List<Dictionary<string, object>> convertDatatableIntoList(DataTable DT)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, object> dict = new Dictionary<string, object>();
            try
            {
                foreach (DataRow row in DT.Rows)
                {
                    dict = new Dictionary<string, object>();
                    foreach (DataColumn col in DT.Columns)
                    {

                        dict.Add(col.ColumnName, row[col]);
                    }
                    list.Add(dict);
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
            }
            return list;
        }
        public Tuple<List<Dictionary<string, object>>, string> getEmployeeDetails()
        {
            string Message = string.Empty;
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            try
            {
                Guid CustomerID = new Guid(_httpContextAccessor.HttpContext!.Session.GetString("CustomerID")!);
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getEmployeDetailstblRegistrationByEmpID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT = new DataTable();
                    DT.Load(Reader);
                    if (DT.Rows.Count > 0)
                    {
                        list = convertDatatableIntoList(DT);
                    }
                }
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(list, Message);
        }
    }
}
