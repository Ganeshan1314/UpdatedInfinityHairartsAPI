using CorePush.Firebase;
using CorePush.Interfaces;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;
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
        private readonly ILogger<LoginService> _logger;
        public LoginService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration, IWebHostEnvironment environment, ILogger<LoginService>? logger = null)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _environment = environment;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LoginService>.Instance;

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
                        "getCustomerProfileByMobileNumber",
                        Parameter,
                        commandType: CommandType.StoredProcedure,
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

        

        public async Task<Tuple<string, List<Dictionary<string, object>>>> CustomerLoginAsync(
            CustomerLoginRequest modal, CancellationToken cancellationToken = default)
        {
            var mobileNo = modal?.MobileNo?.Trim() ?? string.Empty;
            if (!CustomerMobileNumber.IsValid(mobileNo))
                return Tuple.Create("Not Valid MobileNo", new List<Dictionary<string, object>>());

            var clock = Stopwatch.StartNew();
            double openMs = 0, lookupMs = 0, insertMs = 0;
            double stageStartedMs = 0;
            var stage = "connection";
            var completed = false;
            try
            {
                // Keep one connection for this request. The lookup matches the legacy
                // procedure's soft-delete rule, but transfers only the customer ID.
                await using var connection = new SqlConnection(_configuration.GetConnectionString("dbconnection"));
                stageStartedMs = clock.Elapsed.TotalMilliseconds;
                await connection.OpenAsync(cancellationToken);
                openMs = clock.Elapsed.TotalMilliseconds - stageStartedMs;

                stage = "lookup";
                stageStartedMs = clock.Elapsed.TotalMilliseconds;
                var parameters = new DynamicParameters();
                parameters.Add("MobileNo", mobileNo, DbType.String, size: 25);
                var customerId = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                    """
                    SELECT TOP (1) CustomerID
                    FROM dbo.CustomerRegistration
                    WHERE MobileNo = @MobileNo AND (IsDeleted = 0 OR IsDeleted IS NULL)
                    """, parameters, commandTimeout: 5, cancellationToken: cancellationToken));
                lookupMs = clock.Elapsed.TotalMilliseconds - stageStartedMs;

                if (!customerId.HasValue)
                {
                    stage = "insert";
                    stageStartedMs = clock.Elapsed.TotalMilliseconds;
                    customerId = Guid.NewGuid();
                    await connection.ExecuteAsync(new CommandDefinition("customerLoginButtomClick", new
                    {
                        CustomerID = customerId.Value,
                        MobileNo = mobileNo,
                        GeneralAddress = modal?.Address?.Trim() ?? string.Empty
                    }, commandType: CommandType.StoredProcedure, commandTimeout: 5, cancellationToken: cancellationToken));
                    insertMs = clock.Elapsed.TotalMilliseconds - stageStartedMs;
                }

                // Sign-in never rewrites an existing customer's profile, including
                // requests from older app versions that still supply an address.
                cancellationToken.ThrowIfCancellationRequested();
                if (customerId.Value == Guid.Empty)
                    throw new InvalidOperationException("Customer identity is unavailable.");
                _httpContextAccessor.HttpContext!.Session.SetString("CustomerID", customerId.Value.ToString());
                completed = true;
                return Tuple.Create("Success", new List<Dictionary<string, object>>());
            }
            finally
            {
                // Keep the elapsed duration of a failing/cancelled operation too.
                if (!completed)
                {
                    var stageMs = clock.Elapsed.TotalMilliseconds - stageStartedMs;
                    if (stage == "connection") openMs = stageMs;
                    else if (stage == "lookup") lookupMs = stageMs;
                    else if (stage == "insert") insertMs = stageMs;
                }
                // No request body, customer ID, address or SQL exception text is logged.
                _logger.LogInformation(
                    "Customer login database timing: Completed={Completed} Stage={Stage} OpenMs={OpenMs:F1} LookupMs={LookupMs:F1} InsertMs={InsertMs:F1} TotalMs={TotalMs:F1} TraceId={TraceId}",
                    completed, stage, openMs, lookupMs, insertMs, clock.Elapsed.TotalMilliseconds,
                    _httpContextAccessor.HttpContext?.TraceIdentifier);
            }
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
            if (!CustomerEmailAddress.IsValid(modal?.EmailAddress))
            {
                return "Enter a valid Gmail address ending in @gmail.com.";
            }
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
