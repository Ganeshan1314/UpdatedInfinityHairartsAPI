using Dapper;
using Newtonsoft.Json;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Twilio;
using Twilio.Rest.Lookups.V2;
using static InfinityHairartsAPI.Modals.AppoinmentModal;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;

namespace InfinityHairartsAPI.Services
{
    public class AppoinmentService
    {
        SqlConnection con;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        public string selectedCustomerHairCutItem()
        {
            try
            {

            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
            }
            return "";
        }
        public AppoinmentService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
        }
        public SqlConnection DBConnection()
        {
            var con = new SqlConnection(_configuration.GetConnectionString("dbconnection"));
            con.Open();
            return con;
        }
        public Tuple<List<Dictionary<string, object>>, string> getTimeAllocation()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, object> dict = new Dictionary<string, object>();
            DataTable DT = new DataTable();
            string Message = string.Empty;
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    var Reader = con.ExecuteReader("getTimeAllocation", commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT.Load(Reader);
                }
                list = returnList(DT);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                list = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(list, Message);
        }
        public Tuple<List<Dictionary<string, object>>, string> getHairCut_GroupMaster()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, object> dict = new Dictionary<string, object>();
            DataTable DT = new DataTable();
            string Message = string.Empty;
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    var Reader = con.ExecuteReader("getHairCut_GroupMaster", commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT.Load(Reader);
                }
                list = returnList(DT);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                list = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(list, Message);
        }
        public Tuple<List<Dictionary<string, object>>, string> filterHariCutItemGroupMaster(Guid HairCutItemGroupMaster_ID)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, object> dict = new Dictionary<string, object>();
            DataTable DT = new DataTable();
            string Message = string.Empty;
            DataTable DTfilterHariCutItemGroupMaster = new DataTable();
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("HairCutItemGroupMaster_ID", HairCutItemGroupMaster_ID);
                    var Reader = con.ExecuteReader("getHairCutItemGroupMaster", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT = new DataTable();
                    DT.Load(Reader);
                }
                int OrderID = Convert.ToInt32(DT.Rows[0]["OrderID"]);
                if (HairCutItemGroupMaster_ID != Guid.Empty)
                {
                    if (OrderID == 1)
                    {
                        con = DBConnection();
                        SqlCommand cmd = new SqlCommand();
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.CommandTimeout = 0;
                        cmd.Connection = con;
                        cmd.CommandText = "getHairCut_Item_List";
                        SqlDataAdapter adp = new SqlDataAdapter(cmd);
                        DT = new DataTable();
                        DTfilterHariCutItemGroupMaster = new DataTable();
                        adp.Fill(DTfilterHariCutItemGroupMaster);
                        string AWSFolderPath = string.Empty;
                        //string ShowCustomerImage = ConfigurationManager.AppSettings["ShowCustomerImage"];
                        string ShowCustomerImage = _configuration.GetValue<string>("ShowCustomerImage") ?? string.Empty;
                        for (int i = 0; i < DT.Rows.Count; i++)
                        {
                            AWSFolderPath = ShowCustomerImage + Convert.ToString(DT.Rows[i]["HairCutItemImage"]);
                            DT.Rows[i]["HairCutItemImageS3Path"] = AWSFolderPath;
                        }
                    }
                    else
                    {
                        //for (int i = 0; i < HairCutItemGroupMaster_ID.Length; i++)
                        {
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("HairCutItemGroupMaster_ID", HairCutItemGroupMaster_ID);
                                var Reader = con.ExecuteReader("filterHariCutItemGroupMaster", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                                DT = new DataTable();
                                DT.Load(Reader);
                            }
                            DTfilterHariCutItemGroupMaster.Merge(DT, true);
                        }
                    }

                }
                else
                {
                    con = DBConnection();
                    SqlCommand cmd = new SqlCommand();
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandTimeout = 0;
                    cmd.Connection = con;
                    cmd.CommandText = "getHairCut_Item_List";
                    SqlDataAdapter adp = new SqlDataAdapter(cmd);
                    DT = new DataTable();
                    DTfilterHariCutItemGroupMaster = new DataTable();
                    adp.Fill(DTfilterHariCutItemGroupMaster);
                    string AWSFolderPath = string.Empty;
                    //string ShowCustomerImage = ConfigurationManager.AppSettings["ShowCustomerImage"];
                    string ShowCustomerImage = _configuration.GetValue<string>("ShowCustomerImage") ?? string.Empty;
                    for (int i = 0; i < DT.Rows.Count; i++)
                    {
                        AWSFolderPath = ShowCustomerImage + Convert.ToString(DT.Rows[i]["HairCutItemImage"]);
                        DT.Rows[i]["HairCutItemImageS3Path"] = AWSFolderPath;
                    }
                }
                list = returnList(DTfilterHariCutItemGroupMaster);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                list = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(list, Message);
        }
        public List<Dictionary<string, object>> returnList(DataTable DT)
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
        public Tuple<string, List<Dictionary<string, object>>> bookHairCutItemSeat()
        {
            List<Dictionary<string, object>> listbookHairCutItemSeat = new List<Dictionary<string, object>>();
            string Message = string.Empty;
            try
            {

            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                listbookHairCutItemSeat = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(Message, listbookHairCutItemSeat);
        }
        public Tuple<string, int, List<Dictionary<string, object>>> AddtoCartHairCutItem(AddtoCartModal modal)
        {
            string Message = string.Empty;
            int countUniqueBillID = 0;
            Guid HairCut_Item_ID = modal.HairCut_Item_ID;
            List<Dictionary<string, object>> listCartItems = new List<Dictionary<string, object>>();
            try
            {
                Guid UniqueBillID = Guid.NewGuid();
                Guid SeatBookingDetailsID = Guid.Empty;
                DataTable DTgetCountCustomerBill = new DataTable();
                DataTable DTCustomerRegistration = new DataTable();
                int CusotmerBillCount = 0;
                string BillIDFormat = string.Empty;
                string CustomerBillID = "";
                string[] CustomerBillIDArray;
                string UniqueCustomerUserBillID = string.Empty;
                Guid CustomerID = Guid.Empty;
                var varCustomerID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID"));

                removeClientSideCartItem(modal);

                if (!string.IsNullOrWhiteSpace(varCustomerID))
                {
                    CustomerID = new Guid(varCustomerID);
                    var varSeatBookingDetailsID = _httpContextAccessor?.HttpContext?.Session?.GetString("SeatBookingDetailsID");
                    SeatBookingDetailsID = !string.IsNullOrWhiteSpace(varSeatBookingDetailsID) ? new Guid(varSeatBookingDetailsID) : Guid.Empty;
                }
                else
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message, countUniqueBillID, listCartItems);
                }
                DateTime CreateDate = DateTime.Now;
                double Item_Price;
                CustomerBillIDArray = CustomerID.ToString().Split('-');
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.QuerySingle("getCustomerCusotmerBillCount", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    CusotmerBillCount = Reader.UniqueBillID;
                    CusotmerBillCount = CusotmerBillCount + 1;
                }
                CustomerBillID = CustomerBillIDArray[CustomerBillIDArray.Length - 1].ToString() + CusotmerBillCount;
                BillIDFormat = CustomerBillIDArray[CustomerBillIDArray.Length - 1].ToString();
                DataTable DTHairCutItemBillMaster = new DataTable();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getClientBillMasterByCustomerID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTHairCutItemBillMaster = new DataTable();
                    DTHairCutItemBillMaster.Load(Reader);
                }
                if (DTHairCutItemBillMaster.Rows.Count == 0)
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("UniqueBillID", UniqueBillID);
                        //Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                        Parameter.Add("CustomerBillID", CustomerBillID);
                        Parameter.Add("BillIDFormat", BillIDFormat);
                        Parameter.Add("CusotmerBillCount", CusotmerBillCount);
                        Parameter.Add("CreatedBy", CustomerID);
                        Parameter.Add("CreatedDate", CreateDate);
                        con.Execute("InsertUniqueClientSideBillIDValuePrimary", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        _httpContextAccessor?.HttpContext?.Session?.SetString("UniqueBillID", UniqueBillID.ToString());
                        _httpContextAccessor?.HttpContext?.Session?.SetString("CustomerBillID", CustomerBillID.ToString());

                        //HttpContext.Current.Session["UniqueBillID"] = UniqueBillID;
                        //HttpContext.Current.Session["CustomerBillID"] = CustomerBillID;
                    }
                }
                else
                {
                    var varUniqueBillID = DTHairCutItemBillMaster.Rows[0]["UniqueBillID"]?.ToString();
                    UniqueBillID = !string.IsNullOrWhiteSpace(varUniqueBillID) ? new Guid(varUniqueBillID) : Guid.Empty;

                    _httpContextAccessor?.HttpContext?.Session?.SetString("UniqueBillID", UniqueBillID.ToString());
                    _httpContextAccessor?.HttpContext?.Session?.SetString("CustomerBillID", CustomerBillID.ToString());

                    //HttpContext.Current.Session["UniqueBillID"] = UniqueBillID;
                    //HttpContext.Current.Session["CustomerBillID"] = CustomerBillID;
                }
                DataTable DTcountUniquBillidHairCutItemClientSideBillPrimary = new DataTable();
                int seatCount = 0;
                DataSet DS = new DataSet();
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.CommandText = "countHairCutItemClientSideBillPrimaryByUniqueBillID";
                cmd.Parameters.AddWithValue("@SeatBookingDetailsID", SeatBookingDetailsID);
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DS = new DataSet();
                adp.Fill(DS);
                DTcountUniquBillidHairCutItemClientSideBillPrimary = new DataTable();
                DataTable HairCut_Item_Bill_Master = new DataTable();
                DTcountUniquBillidHairCutItemClientSideBillPrimary = DS.Tables[0];
                HairCut_Item_Bill_Master = DS.Tables[1];

                if (DTcountUniquBillidHairCutItemClientSideBillPrimary.Rows.Count > 0)
                {
                    seatCount = Convert.ToInt32(DTcountUniquBillidHairCutItemClientSideBillPrimary.Rows[0]["SeatCount"]);
                }
                else
                {
                    seatCount = 0;
                }
                if (seatCount > HairCut_Item_Bill_Master.Rows.Count)
                {
                    using (con = DBConnection())
                    {
                        var SelectItemPrice = new DynamicParameters();
                        SelectItemPrice.Add("HairCut_Item_ID", HairCut_Item_ID);
                        var ItemPrice_Select = con.QuerySingle("selectHairCut_Item_Price", SelectItemPrice, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        Item_Price = Convert.ToDouble(ItemPrice_Select.HairCut_Item_Price);
                        Guid HairCut_Item_Customer_Unique_ID = Guid.NewGuid();
                        double GST = (double)18 / 100;
                        double HairCut_Item_CGST = (GST * Convert.ToDouble(Item_Price)) / 2;
                        double HairCut_Item_Taxable = Convert.ToDouble(Item_Price) - (HairCut_Item_CGST * 2);
                        DateTime Created_Date = DateTime.Now;

                        var varCreated_By = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
                        Guid Created_By = !string.IsNullOrWhiteSpace(varCreated_By) ?  new Guid(varCreated_By) : Guid.Empty;
                        
                        
                        var ParameterInsertHairCutItemCustomerBill = new DynamicParameters();
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_Customer_Unique_ID", HairCut_Item_Customer_Unique_ID);

                        var varUniqueBillID = _httpContextAccessor?.HttpContext?.Session?.GetString("UniqueBillID");


                        //if (Convert.ToString(HttpContext.Current.Session["UniqueBillID"]).Trim() == "")
                        if (string.IsNullOrWhiteSpace(varUniqueBillID))
                        {
                            ParameterInsertHairCutItemCustomerBill.Add("UniqueBillID", UniqueBillID);
                        }
                        else
                        {
                            UniqueBillID = new Guid(varUniqueBillID);
                            ParameterInsertHairCutItemCustomerBill.Add("UniqueBillID", UniqueBillID);
                        }
                        
                        var varCustomerBillID = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerBillID");

                        if (string.IsNullOrWhiteSpace(varCustomerBillID))
                        {
                            ParameterInsertHairCutItemCustomerBill.Add("CustomerBillID", CustomerBillID);
                        }
                        else
                        {
                            ParameterInsertHairCutItemCustomerBill.Add("CustomerBillID", varCustomerBillID);
                        }
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_ID", HairCut_Item_ID);
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_Quantity", 1);
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_Total", Item_Price);
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_CGST", HairCut_Item_CGST);
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_SGST", HairCut_Item_CGST);
                        ParameterInsertHairCutItemCustomerBill.Add("HairCut_Item_Taxable", HairCut_Item_Taxable);
                        ParameterInsertHairCutItemCustomerBill.Add("Created_Date", CreateDate);
                        ParameterInsertHairCutItemCustomerBill.Add("Created_By", CustomerID);
                        con.Execute("InsertHairCutItemClientSideBillPrimary", ParameterInsertHairCutItemCustomerBill, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    }
                    Message = "Success";
                }
                else
                {
                    Message = "Cart Item Full";
                }
                DataTable DTCartItems = new DataTable();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getClientSideCartItems", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTCartItems.Load(Reader);
                }
                listCartItems = convertDatatabletoList(DTCartItems);
                countUniqueBillID = Convert.ToInt32(DTCartItems.Rows[0]["TotalCartItemCount"]);

            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
                listCartItems = new List<Dictionary<string, object>>();
            }
            return Tuple.Create(Message, countUniqueBillID, listCartItems);
        }
        public Tuple<string, List<Dictionary<string, object>>, string> getCustomerCart()
        {
            string Message = string.Empty;
            List<Dictionary<string, object>> listCartItems = new List<Dictionary<string, object>>();
            DataTable DTCartItems = new DataTable();
            Guid CustomerID = Guid.Empty;
            string itemSelectedOrNot = string.Empty;
            //_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
            var varCustomerID = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
            CustomerID = !string.IsNullOrWhiteSpace(varCustomerID) ? new Guid(varCustomerID) : Guid.Empty;
            try
            {
                if (CustomerID == Guid.Empty)
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message, listCartItems, "");
                }
                
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getClientSideCartItems", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTCartItems.Load(Reader);
                }
                if (DTCartItems.Rows.Count == 0)
                {
                    var response = checkCustomerSelectedSeatOrNot();
                    itemSelectedOrNot = response.Item1;
                }

                listCartItems = convertDatatabletoList(DTCartItems);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
                listCartItems = new List<Dictionary<string, object>>();
            }
            return Tuple.Create(Message, listCartItems, itemSelectedOrNot);
        }
        // Confirmed booking info (haircut item name, date/time) survives even after the
        // session cart is cleared, since it is looked up by UniqueBillID rather than the live cart.
        public Tuple<string, List<Dictionary<string, object>>> getCustomerBookingDetails()
        {
            string Message = string.Empty;
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            try
            {
                Guid CustomerID = getSessionvalueCustomerID();
                if (CustomerID == Guid.Empty)
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message, list);
                }

                //Guid UniqueBillID = getSessionvalueUniqueBillID();
                //if (UniqueBillID == Guid.Empty)
                //{
                //    DataTable DTCustomerMasterPrimary = new DataTable();
                //    using (con = DBConnection())
                //    {
                //        var Parameter = new DynamicParameters();
                //        Parameter.Add("CustomerID", CustomerID);
                //        var Reader = con.ExecuteReader("getClientBillMasterByCustomerID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                //        DTCustomerMasterPrimary.Load(Reader);
                //    }
                //    if (DTCustomerMasterPrimary.Rows.Count > 0)
                //    {
                //        Guid.TryParse(DTCustomerMasterPrimary.Rows[0]["UniqueBillID"]?.ToString(), out UniqueBillID);
                //    }
                //}

                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getCustomerBookingInformation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT.Load(Reader);
                }
                // If no rows yet, retry with Guid.Empty so the SP can return any booking for this CustomerID.
                //if (DT.Rows.Count == 0 && UniqueBillID != Guid.Empty)
                //{
                //    DataTable DTFallback = new DataTable();
                //    try
                //    {
                //        using (con = DBConnection())
                //        {
                //            var Parameter = new DynamicParameters();
                //            Parameter.Add("CustomerID", CustomerID);
                //            Parameter.Add("UniqueBillID", Guid.Empty);
                //            var Reader = con.ExecuteReader("getCustomerBookingInformation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                //            DTFallback.Load(Reader);
                //        }
                //        if (DTFallback.Rows.Count > 0) DT = DTFallback;
                //    }
                //    catch { }
                //}
                list = convertDatatabletoList(DT);
                Message = "Success";
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
        public Tuple<string> checkCustomerSelectedSeatOrNot()
        {
            string Message = string.Empty;
            DataTable DT = new DataTable();
            Guid CustomerID = Guid.Empty;
            try
            {
                var varCustomerID = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
                CustomerID = !string.IsNullOrWhiteSpace(varCustomerID) ? Guid.Parse(varCustomerID) : Guid.Empty;

                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("checkCustomerSelectedSeatOrNot", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT.Load(Reader);
                }
                if (DT.Rows.Count == 0)
                {
                    Message = "Not Selected";
                }
                else
                {
                    Message = "Selected";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message);
        }
        private Guid getSessionvalueCustomerID()
        {
            Guid CustomerID = Guid.Empty;
            try
            {
                var varCustomerID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID"));
                CustomerID = !string.IsNullOrEmpty(varCustomerID) ? new Guid(varCustomerID) : Guid.Empty;
            }
            catch (Exception)
            {

            }   
            
            return CustomerID;
        }
        
        private Guid getSessionvalueUniqueBillID()
        {
            Guid UniqueBillID = Guid.Empty;
            try
            {
                var varUniqueBillID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("UniqueBillID"));
                UniqueBillID = !string.IsNullOrEmpty(varUniqueBillID) ? new Guid(varUniqueBillID) : Guid.Empty;
            }
            catch (Exception)
            {

            }

            return UniqueBillID;
        }
        public Tuple<string, int, List<Dictionary<string, object>>> removeClientSideCartItem(AddtoCartModal modal)
        {
            string Message = string.Empty;
            int countUniqueBillID = 0;
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            try
            {
                Guid CustomerID = getSessionvalueCustomerID();
                if (CustomerID == Guid.Empty)
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message, countUniqueBillID, list);
                }
                Message = "Success";
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    Parameter.Add("seatCount", modal.seatCount);
                    con.Execute("deleteClientSideCartItem", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                }
                DataTable DTCartItems = new DataTable();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("getClientSideCartItems", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTCartItems.Load(Reader);
                }
                list = convertDatatabletoList(DTCartItems);
                if (DTCartItems.Rows.Count > 0)
                {
                    countUniqueBillID = Convert.ToInt32(DTCartItems.Rows[0]["TotalCartItemCount"]);
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
                list = new List<Dictionary<string, object>>();
            }
            return Tuple.Create(Message, countUniqueBillID, list);
        }
        public Tuple<string, int, List<Dictionary<string, object>>> getClientSideCartItemPageLoad()
        {
            string Message = string.Empty;
            int countUniqueBillID = 0;
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                Guid CustomerID = Guid.Empty;
                if (getSessionvalueCustomerID() != Guid.Empty)
                {
                    CustomerID = getSessionvalueCustomerID();
                    DataTable DTCartItems = new DataTable();
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("getClientSideCartItemPageLoad", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTCartItems.Load(Reader);
                    }
                    list = convertDatatabletoList(DTCartItems);
                    if (DTCartItems.Rows.Count > 0)
                    {
                        countUniqueBillID = Convert.ToInt32(DTCartItems.Rows[0]["TotalCartItemCount"]);
                    }
                    Message = "Success";
                }
                else
                {
                    list = new List<Dictionary<string, object>>();
                    countUniqueBillID = 0;
                    Message = "Empty";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
                list = new List<Dictionary<string, object>>();
            }
            return Tuple.Create(Message, countUniqueBillID, list);
        }
        public List<Dictionary<string, object>> convertDatatabletoList(DataTable DT)
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
        public List<Dictionary<string, object>> getHairCutItemName(string HairCutItemName)
        {
            List<Dictionary<string, object>> listGetHairCutItemNameCode = new List<Dictionary<string, object>>();
            DataTable DTGetHairCutItemNameCode = new DataTable();
            try
            {
                using (con = DBConnection())
                {
                    var ParameterGetHairCutItemNameCode = new DynamicParameters();
                    ParameterGetHairCutItemNameCode.Add("HairCutItemName", HairCutItemName);
                    var ReaderGetHairCutItemNameCode = con.ExecuteReader("AutocompleteGetHairCutItemByName", ParameterGetHairCutItemNameCode, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTGetHairCutItemNameCode = new DataTable();
                    DTGetHairCutItemNameCode.Load(ReaderGetHairCutItemNameCode);
                    listGetHairCutItemNameCode = convertDatatabletoList(DTGetHairCutItemNameCode);
                }
                return listGetHairCutItemNameCode;
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                listGetHairCutItemNameCode = new List<Dictionary<string, object>>();
                return listGetHairCutItemNameCode;
            }
        }
        public Tuple<string, List<Dictionary<string, object>>> getSearchHairCutItemAutoComplete(Guid HairCut_Item_ID)
        {
            List<Dictionary<string, object>> listGetHairCutItemNameCode = new List<Dictionary<string, object>>();
            DataTable DTGetHairCutItemNameCode = new DataTable();
            string Message = string.Empty;
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("HairCut_Item_ID", HairCut_Item_ID);
                    var ReaderGetHairCutItemNameCode = con.ExecuteReader("getSearchHairCutItemAutoComplete", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTGetHairCutItemNameCode = new DataTable();
                    DTGetHairCutItemNameCode.Load(ReaderGetHairCutItemNameCode);
                    listGetHairCutItemNameCode = convertDatatabletoList(DTGetHairCutItemNameCode);
                }
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                listGetHairCutItemNameCode = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(Message, listGetHairCutItemNameCode);
        }
        public Tuple<string, List<Dictionary<string, object>>> getHairCutItemListByName(string HairCut_Item_Name)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            string Message = string.Empty;
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("HairCut_Item_Name", HairCut_Item_Name);
                    var Reader = con.ExecuteReader("getHairCutItemListByName", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT = new DataTable();
                    DT.Load(Reader);
                }
                Message = "Success";
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
        public Tuple<string, List<Dictionary<string, object>>, int, List<Dictionary<string, object>>, List<Dictionary<string, object>>> getSeatTimeAllocation()
        {
            string Message = string.Empty;
            DataTable DT = new DataTable();
            DataTable DTSelectedTimeAllocation = new DataTable();
            DataTable DTSeatBookingDetails = new DataTable();
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> listSeatBookingDetail = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> listSelectedTimeAllocation = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> listDateValues = new List<Dictionary<string, object>>();
            string[] dateArray = new string[4];
            int SeatBookingDetailsCount = 0;
            string BookingDate = string.Empty;
            bool showTomorrowOnly = false;
            try
            {
                string today = DateTime.Now.ToString("dd-MMM");
                string tomorrow = Convert.ToDateTime(today).AddDays(1).ToString("dd-MMM");
                string todayDayOftheWeek = DateTime.Now.ToString("dddd");
                string tomorrowDayOftheWeek = Convert.ToDateTime(today).AddDays(1).ToString("dddd");

                dateArray[0] = today;
                dateArray[1] = tomorrow;

                Guid CustomerID = Guid.Empty;
                Guid SeatBookingDetailsID = Guid.Empty;
                bool BookingAvailableStatus;
                string TodayBooking = string.Empty;
                if (getSessionvalueCustomerID() != Guid.Empty)
                {
                    CustomerID = getSessionvalueCustomerID();
                }
                else
                {
                    Message = "Session Expired";
                }
                using (con = DBConnection())
                {
                    var Reader = con.ExecuteReader("getSeatTimeAllocation", commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DT = new DataTable();
                    DT.Load(Reader);
                    DataColumn column = new DataColumn();
                    column.DataType = typeof(string);
                    column.ColumnName = "EnableDisable";
                    DT.Columns.Add(column);
                    DataColumn bookedColumn = new DataColumn("BookedByOtherCustomer", typeof(bool));
                    bookedColumn.DefaultValue = false;
                    DT.Columns.Add(bookedColumn);
                    TimeSpan currentTime = DateTime.Now.TimeOfDay;
                    TimeSpan eightOClock = new TimeSpan(18, 01, 0);
                    if (currentTime > eightOClock || todayDayOftheWeek == "Friday")
                    {
                        TodayBooking = "Completed";
                        if (tomorrowDayOftheWeek == "Friday")
                        {
                            tomorrow = Convert.ToDateTime(today).AddDays(2).ToString("dd-MMM");
                            dateArray[0] = tomorrow;
                            BookingDate = Convert.ToDateTime(today).AddDays(2).ToString();
                        }
                        else
                        {
                            dateArray[0] = tomorrow;
                            BookingDate = Convert.ToDateTime(today).AddDays(1).ToString();
                        }

                    }
                    else
                    {
                        TodayBooking = "Not Completed";
                        if (todayDayOftheWeek == "Friday")
                        {
                            today = Convert.ToDateTime(today).AddDays(1).ToString("dd-MMM");
                            dateArray[0] = today;
                            BookingDate = Convert.ToDateTime(today).AddDays(1).ToString();
                        }
                        else
                        {
                            BookingDate = Convert.ToDateTime(today).ToString();
                        }
                    }
                    currentTime = currentTime.Add(new TimeSpan(1, 30, 0));
                    for (int i = 0; i < DT.Rows.Count; i++)
                    {
                        TimeSpan FullTiming;
                        if (DateTime.TryParseExact(DT.Rows[i]["FullTiming"].ToString(), "hh:mm tt", null, System.Globalization.DateTimeStyles.None, out DateTime dateTimeValue))
                        {
                            FullTiming = dateTimeValue.TimeOfDay;
                        }
                        else
                        {
                            FullTiming = TimeSpan.Zero;
                        }
                        if (currentTime > FullTiming && TodayBooking == "Not Completed")
                        {
                            DT.Rows[i]["EnableDisable"] = "Disable";
                        }
                        else
                        {
                            BookingAvailableStatus = Convert.ToBoolean(DT.Rows[i]["BookingAvailableStatus"]);
                            if (BookingAvailableStatus == true)
                            {
                                DT.Rows[i]["EnableDisable"] = "Enable";
                            }
                            else
                            {
                                DT.Rows[i]["EnableDisable"] = "Disable";
                            }

                        }
                    }

                    // If every slot for today has passed, show tomorrow's slots from the morning.
                    if (TodayBooking == "Not Completed" &&
                        !DT.AsEnumerable().Any(row => row["EnableDisable"].ToString() == "Enable"))
                    {
                        TodayBooking = "Completed";
                        showTomorrowOnly = true;
                        dateArray[0] = tomorrow;
                        BookingDate = Convert.ToDateTime(today).AddDays(1).ToString();

                        for (int i = 0; i < DT.Rows.Count; i++)
                        {
                            BookingAvailableStatus = Convert.ToBoolean(DT.Rows[i]["BookingAvailableStatus"]);
                            DT.Rows[i]["EnableDisable"] = BookingAvailableStatus ? "Enable" : "Disable";
                        }
                    }

                    list = convertDatatabletoList(DT);
                }
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("checkcustomerIDSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTSeatBookingDetails = new DataTable();
                    DTSeatBookingDetails.Load(Reader);
                    if (DTSeatBookingDetails.Rows.Count > 0)
                    {
                        SeatBookingDetailsCount = Convert.ToInt32(DTSeatBookingDetails.Rows[0]["SeatCount"]);
                        _httpContextAccessor?.HttpContext?.Session?.SetString("SeatBookingDetailsID", DTSeatBookingDetails.Rows[0]["SeatBookingDetailsID"].ToString());
                        //HttpContext.Current.Session["SeatBookingDetailsID"] = new Guid(DTSeatBookingDetails.Rows[0]["SeatBookingDetailsID"].ToString());
                        SeatBookingDetailsID = new Guid(DTSeatBookingDetails.Rows[0]["SeatBookingDetailsID"].ToString());
                    }
                }
                // Do not show a slot already selected for this booking date by any customer.
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("BookingDate", Convert.ToDateTime(BookingDate).Date);
                      Parameter.Add("CurrentSeatBookingDetailsID", SeatBookingDetailsID);
                    var Reader = con.ExecuteReader(@"
                        SELECT DISTINCT CT.TimeAllocationID
                        FROM CustomerTimeSelection CT
                        INNER JOIN SeatBookingDetails SB ON SB.SeatBookingDetailsID = CT.SeatBookingDetailsID
                        WHERE CONVERT(date, SB.BookingDate) = @BookingDate
                          AND SB.SeatBookingDetailsID <> @CurrentSeatBookingDetailsID
                          AND SB.BookingStatusMasterID = 2
                                                    
                        ", Parameter, commandType: CommandType.Text, commandTimeout: 0);
                    var bookedByOtherCustomer = new DataTable();
                    bookedByOtherCustomer.Load(Reader);

                    var bookedIds = new HashSet<string>(
                        bookedByOtherCustomer.AsEnumerable()
                            .Select(row => row["TimeAllocationID"].ToString()),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (DataRow timeAllocation in DT.Rows)
                    {
                        if (bookedIds.Contains(timeAllocation["TimeAllocationID"].ToString()))
                        {
                            timeAllocation["EnableDisable"] = "Disable";
                            timeAllocation["BookedByOtherCustomer"] = true;
                        }
                    }
                }

                DateTime CreatedOn = DateTime.Today;
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    var Reader = con.ExecuteReader("getSelectedTimeAllocation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTSelectedTimeAllocation = new DataTable();
                    DTSelectedTimeAllocation.Load(Reader);

                    if (DTSelectedTimeAllocation.Columns.Contains("CreatedOn"))
                    {
                        for (int i = DTSelectedTimeAllocation.Rows.Count - 1; i >= 0; i--)
                        {
                            if (DateTime.TryParse(DTSelectedTimeAllocation.Rows[i]["CreatedOn"]?.ToString(), out var createdOn) &&
                                createdOn < DateTime.Now.AddMinutes(-10))
                            {
                                DTSelectedTimeAllocation.Rows.RemoveAt(i);
                            }
                        }
                    }
                }
                if (DTSelectedTimeAllocation.Rows.Count == 0)
                {
                    SeatBookingDetailsCount = 0;
                }
                else
                {
                    for (int i = DTSelectedTimeAllocation.Rows.Count - 1; i >= 0; i--)
                    {
                        if (DTSelectedTimeAllocation.Columns.Contains("SeatBookingDetailsID") &&
                            Guid.TryParse(DTSelectedTimeAllocation.Rows[i]["SeatBookingDetailsID"]?.ToString(), out var selectedBookingId) &&
                            selectedBookingId == SeatBookingDetailsID)
                        {
                            DTSelectedTimeAllocation.Rows[i].Delete();
                        }
                        else if (DTSelectedTimeAllocation.Columns.Contains("BookingDate") &&
                                 Convert.ToDateTime(DTSelectedTimeAllocation.Rows[i]["BookingDate"]).Date != Convert.ToDateTime(BookingDate).Date)
                        {
                            DTSelectedTimeAllocation.Rows[i].Delete();
                        }
                    }

                    DTSelectedTimeAllocation.AcceptChanges();

                    if (DTSelectedTimeAllocation.Rows.Count == 0)
                    {
                        SeatBookingDetailsCount = 0;
                    }

                    for (int i = DTSelectedTimeAllocation.Rows.Count - 1; i >= 0; i--)
                    {
                        if (TodayBooking == "Completed")
                        {
                            DataRow dr = DTSelectedTimeAllocation.Rows[i];
                            dateArray[0] = tomorrow;
                            if (Convert.ToDateTime(dr["BookingDate"]).Date != Convert.ToDateTime(tomorrow).Date)
                            {
                                dr.Delete();
                            }
                        }
                    }

                    var selectedTimeIds = new HashSet<string>(
                        DTSelectedTimeAllocation.AsEnumerable()
                            .Select(row => Convert.ToString(row["TimeAllocationID"]))
                            .Where(id => !string.IsNullOrWhiteSpace(id)),
                        StringComparer.OrdinalIgnoreCase);

                    int seatSelectionCount = 0;
                    if (DTSeatBookingDetails.Rows.Count > 0 && DTSeatBookingDetails.Columns.Contains("SeatCount") &&
                        int.TryParse(Convert.ToString(DTSeatBookingDetails.Rows[0]["SeatCount"]), out var currentSeatCount))
                    {
                        seatSelectionCount = currentSeatCount;
                    }

                    if (seatSelectionCount > 0 && selectedTimeIds.Count >= seatSelectionCount)
                    {
                        foreach (DataRow timeAllocation in DT.Rows)
                        {
                            var timeAllocationId = Convert.ToString(timeAllocation["TimeAllocationID"]);
                            if (!selectedTimeIds.Contains(timeAllocationId))
                            {
                                timeAllocation["EnableDisable"] = "Disable";
                            }
                        }
                    }

                    listSelectedTimeAllocation = convertDatatabletoList(DTSelectedTimeAllocation);

                }
                list = convertDatatabletoList(DT);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = list.Count > 0 ? "Success" : "Error";
            }
            dateArray[2] = BookingDate;
            DataTable DTdateValues = new DataTable();
            DTdateValues.Columns.Add("Date",typeof(string));
            DTdateValues.Columns.Add("Day",typeof(string));
            DTdateValues.Columns.Add("Month",typeof(string));
            DTdateValues.Columns.Add("Year",typeof(string));
            DTdateValues.Columns.Add("FullDate",typeof(string));
            for (int i = 0; i < 5; i++)
            {
                DateTime now = DateTime.Now;
                DateTime eveningSix = DateTime.Today.AddHours(18);
                if (now > eveningSix && i == 0)
                {
                    i++;
                }
                DateTime date = DateTime.Today.AddDays(i + (showTomorrowOnly ? 1 : 0));

                string day = date.DayOfWeek.ToString();   // Monday
                string month = date.ToString("MMMM");     // January
                string fullDate = date.ToString("dd");
                string year = date.ToString("yyyy");
                string FullDate = date.ToString();
                if(day != "Friday")
                {
                    DTdateValues.Rows.Add(fullDate, day, month,year, FullDate);
                }
            }
            listDateValues = convertDatatabletoList(DTdateValues);


            return Tuple.Create(Message, list, SeatBookingDetailsCount, listSelectedTimeAllocation, listDateValues);
        }
        public Tuple<string, int, List<Dictionary<string, object>>, List<string>> insertupdateSeatBookingDetails(int SeatCount, DateTime BookingDate)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            DataTable DTSeatBookingDetails = new DataTable();
            DataTable DTCustomerTimeSelection = new DataTable();
            List<string> TimeAllocationIDList = new List<string>();
            string Message = "";
            Guid CustomerID = Guid.Empty;
            if (getSessionvalueCustomerID() != Guid.Empty)
            {
                CustomerID = getSessionvalueCustomerID();
            }
            else
            {
                Message = "Session Expired";
            }
            Guid SeatBookingDetailsID = Guid.Empty;
            if (Message == "")
            {
                try
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("checkcustomerIDSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTSeatBookingDetails = new DataTable();
                        DTSeatBookingDetails.Load(Reader);
                    }
                    if (DTSeatBookingDetails.Rows.Count == 0)
                    {
                        SeatBookingDetailsID = Guid.NewGuid();
                        _httpContextAccessor?.HttpContext?.Session?.SetString("SeatBookingDetailsID", SeatBookingDetailsID.ToString());
                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                            Parameter.Add("CustomerID", CustomerID);
                            Parameter.Add("SeatCount", SeatCount);
                            Parameter.Add("BookingStatusMasterID", 1);
                            Parameter.Add("BookingDate", BookingDate);
                            Parameter.Add("CreatedOn", DateTime.Now);
                            con.Execute("insertSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        }
                    }
                    else if (DTSeatBookingDetails.Rows.Count == 1)
                    {
                        // An existing row is only a still-editable draft of THIS SAME booking
                        // if its date matches what's being submitted. A pending row for a
                        // different date is an already-confirmed, not-yet-billed appointment
                        // (BookingStatusMasterID stays 1 until staff bills it) - reusing it here
                        // would silently move that appointment's date and delete its time slot,
                        // making it look available to other customers even though it's booked.
                        bool isSameDraft = DTSeatBookingDetails.Columns.Contains("BookingDate") &&
                            DateTime.TryParse(DTSeatBookingDetails.Rows[0]["BookingDate"]?.ToString(), out var existingBookingDate) &&
                            existingBookingDate.Date == BookingDate.Date;

                        if (!isSameDraft)
                        {
                            SeatBookingDetailsID = new Guid(DTSeatBookingDetails.Rows[0]["SeatBookingDetailsID"].ToString());
                            _httpContextAccessor?.HttpContext?.Session?.SetString("SeatBookingDetailsID", SeatBookingDetailsID.ToString());
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                                Parameter.Add("SeatCount", SeatCount);
                                Parameter.Add("BookingDate", BookingDate);
                                Parameter.Add("CreatedOn", DateTime.Now);
                                con.Execute("updateSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            }
                        }
                        else
                        {
                            SeatBookingDetailsID = new Guid(DTSeatBookingDetails.Rows[0]["SeatBookingDetailsID"].ToString());
                            _httpContextAccessor?.HttpContext?.Session?.SetString("SeatBookingDetailsID", SeatBookingDetailsID.ToString());
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                                Parameter.Add("SeatCount", SeatCount);
                                Parameter.Add("BookingDate", BookingDate);
                                Parameter.Add("CreatedOn", DateTime.Now);
                                con.Execute("updateSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            }
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                                Parameter.Add("CustomerID", CustomerID);
                                DTCustomerTimeSelection = new DataTable();
                                var Reader = con.ExecuteReader("countCustomerTimeSelection", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                                DTCustomerTimeSelection.Load(Reader);
                            }
                            if (DTCustomerTimeSelection.Rows.Count > 0)
                            {
                                using (con = DBConnection())
                                {
                                    var Parameter = new DynamicParameters();
                                    Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                                    Parameter.Add("CustomerID", CustomerID);
                                    con.Execute("deleteCustomerTimeSelectionBySeatBookingDetailsID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                                }
                            }
                        }
                    }
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        con.Execute("deleteCartItemByCustomerID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    }
                    list = new List<Dictionary<string, object>>();
                    list = convertDatatabletoList(DTCustomerTimeSelection);
                    Message = "Success";
                    for (int i = 0; i < DTCustomerTimeSelection.Rows.Count; i++)
                    {
                        TimeAllocationIDList.Add(DTCustomerTimeSelection.Rows[i]["TimeAllocationID"].ToString());
                    }
                }
                catch (Exception ex)
                {
                    LogError log = new LogError();
                    log.Error(ex);
                    list = new List<Dictionary<string, object>>();
                    Message = "Error";
                }
            }
            return Tuple.Create(Message, SeatCount, list, TimeAllocationIDList);
        }
        public Tuple<string, Guid, string> insertCustomerTimeSelection(Guid TimeAllocationID)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            string Message = "";
            Guid CustomerID = Guid.Empty;
            Guid SeatBookingDetailsID = Guid.Empty;
            Guid TimeSelectionID = Guid.NewGuid();
            int countseatsTimeSelection = 0;
            int seatCount = 0;
            DataTable DTTimeAllocation = new DataTable();
            string seatType = "";
            string isDeleteRequired = "";
            try
            {

                if (getSessionvalueCustomerID() != Guid.Empty)
                {
                    CustomerID = getSessionvalueCustomerID();
                    using(con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var seatbookingdetail = con.ExecuteReader("getSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTTimeAllocation = new DataTable();
                        DTTimeAllocation.Load(seatbookingdetail);
                        SeatBookingDetailsID = new Guid(DTTimeAllocation.Rows[0]["SeatBookingDetailsID"].ToString());
                    }
                    //SeatBookingDetailsID = getSessionvalueSeatBookingDetailsID();
                }
                else
                {
                    Message = "Session Expired";
                }
                if (Message == "")
                {

                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("TimeAllocationID", TimeAllocationID);
                        Parameter.Add("CustomerID", CustomerID);
                        Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                        // Only treat the slot as booked when the conflicting selection belongs to the
                        // same calendar date as the current customer's own booking. TimeAllocationID
                        // values are reused every day, so comparing without the date scope caused every
                        // slot to be reported as booked once any customer had ever picked that time.
                        var alreadySelected = con.ExecuteScalar<int>(@"
                            SELECT COUNT(1)
                            FROM CustomerTimeSelection CT
                            INNER JOIN SeatBookingDetails SB ON SB.SeatBookingDetailsID = CT.SeatBookingDetailsID
                            INNER JOIN SeatBookingDetails MySB ON MySB.SeatBookingDetailsID = @SeatBookingDetailsID
                            WHERE CT.TimeAllocationID = @TimeAllocationID
                              AND CT.CustomerID <> @CustomerID
                                                            AND CAST(SB.BookingDate AS DATE) = CAST(MySB.BookingDate AS DATE)
                                                            AND SB.CreatedOn >= DATEADD(MINUTE, -10, GETDATE())", Parameter, commandType: CommandType.Text, commandTimeout: 0);

                        if (alreadySelected > 0)
                        {
                            return Tuple.Create("Booked", TimeAllocationID, "");
                        }
                    }

                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        //Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("countCustomerTimeSelection", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTTimeAllocation = new DataTable();
                        DTTimeAllocation.Load(Reader);
                        countseatsTimeSelection = Convert.ToInt32(DTTimeAllocation.Rows.Count);
                    }
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var count = con.QuerySingle("getSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        seatCount = count.SeatCount;
                    }
                    if (countseatsTimeSelection <= seatCount)
                    {
                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                            Parameter.Add("CustomerID", CustomerID);
                            Parameter.Add("TimeAllocationID", TimeAllocationID);
                            var Reader = con.ExecuteReader("countCustomerTimeAllocation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            DT.Load(Reader);
                        }
                        if (DT.Rows.Count == 0)
                        {
                            using (con = DBConnection())
                            {
                                var Parameter = new DynamicParameters();
                                Parameter.Add("TimeSelectionID", TimeSelectionID);
                                Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                                Parameter.Add("CustomerID", CustomerID);
                                Parameter.Add("TimeAllocationID", TimeAllocationID);
                                con.Execute("insertCustomerTimeSelection", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            }
                            isDeleteRequired = "True";
                        }
                        else
                        {
                            isDeleteRequired = "False";

                        }
                    }
                    if (countseatsTimeSelection == seatCount && isDeleteRequired == "True")
                    {
                        TimeSelectionID = new Guid(DTTimeAllocation.Rows[0]["TimeSelectionID"].ToString());
                        TimeAllocationID = new Guid(DTTimeAllocation.Rows[0]["TimeAllocationID"].ToString());
                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("TimeSelectionID", TimeSelectionID);
                            con.Execute("deleteCustomerTimeSelection", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        }
                        seatType = "Replace";
                    }

                    Message = "Success";

                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                list = new List<Dictionary<string, object>>();
                Message = "Error";
            }
            return Tuple.Create(Message, TimeAllocationID, seatType);
        }
        public Tuple<string, Guid> deleteCustomerTimeSelection(Guid TimeAllocationID)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DT = new DataTable();
            string Message = "";
            Guid CustomerID = Guid.Empty;
            Guid SeatBookingDetailsID = Guid.Empty;
            Guid TimeSelectionID = Guid.NewGuid();
            DataTable DTTimeAllocation = new DataTable();
            if (getSessionvalueCustomerID() != Guid.Empty)
            {
                CustomerID = getSessionvalueCustomerID();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var seatbookingdetail = con.ExecuteReader("getSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DataTable DTSeatBooking = new DataTable();
                    DTSeatBooking.Load(seatbookingdetail);
                    SeatBookingDetailsID = new Guid(DTSeatBooking.Rows[0]["SeatBookingDetailsID"].ToString());
                }
                //SeatBookingDetailsID = getSessionvalueSeatBookingDetailsID();
            }
            else
            {
                Message = "Session Expired";
            }
            if (Message == "")
            {
                try
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                        Parameter.Add("TimeAllocationID", TimeAllocationID);
                        con.Execute("deleteCustomerTimeSelectionByTimeAllocationID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    }
                    Message = "Success";
                }
                catch (Exception ex)
                {
                    LogError log = new LogError();
                    log.Error(ex);
                    list = new List<Dictionary<string, object>>();
                    Message = "Error";
                }
            }
            return Tuple.Create(Message, TimeAllocationID);
        }
        public Tuple<string, string, int> goNextSeatSelection()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DTSeatBookingDetails = new DataTable();
            DataTable DTTimeSelection = new DataTable();
            string Message = "";
            string seatSelectionstatus = "";
            Guid CustomerID = Guid.Empty;
            Guid SeatBookingDetailsID = Guid.Empty;
            int SeatCount = 0;
            if (getSessionvalueCustomerID() != Guid.Empty)
            {
                CustomerID = getSessionvalueCustomerID();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("CustomerID", CustomerID);
                    var seatbookingdetail = con.ExecuteReader("getSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DataTable DTTimeAllocation = new DataTable();
                    DTTimeAllocation.Load(seatbookingdetail);
                    SeatBookingDetailsID = new Guid(DTTimeAllocation.Rows[0]["SeatBookingDetailsID"].ToString());
                }

                //SeatBookingDetailsID = getSessionvalueSeatBookingDetailsID();
            }
            else
            {
                Message = "Session Expired";
            }
            if (Message == "")
            {
                try
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        //Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("countCustomerTimeSelection", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTTimeSelection = new DataTable();
                        DTTimeSelection.Load(Reader);
                    }
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("getSeatBookingDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTSeatBookingDetails = new DataTable();
                        DTSeatBookingDetails.Load(Reader);
                    }
                    SeatCount = Convert.ToInt32(DTSeatBookingDetails.Rows[0]["SeatCount"]);
                    if (DTTimeSelection.Rows.Count == SeatCount)
                    {
                        seatSelectionstatus = "Completed";
                    }
                    else if (DTTimeSelection.Rows.Count < SeatCount)
                    {
                        seatSelectionstatus = "Lower";
                    }
                    else if (DTTimeSelection.Rows.Count > SeatCount)
                    {
                        seatSelectionstatus = "Higher";
                    }
                    Message = "Success";
                }
                catch (Exception ex)
                {
                    LogError log = new LogError();
                    log.Error(ex);
                    list = new List<Dictionary<string, object>>();
                    Message = "Error";
                }
            }
            return Tuple.Create(Message, seatSelectionstatus, SeatCount);
        }
        public Tuple<string, List<Dictionary<string, object>>> loadSeatBookingDetailsDatewise(DateTime selectedDate)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            DataTable DTSeatBookingDetailsDatewise = new DataTable();
            DataTable DTTimeSelection = new DataTable();
            string Message = "";
            //Guid CustomerID = Guid.Empty;
            //Guid SeatBookingDetailsID = Guid.Empty;
            var SeatBookingDetailsID= Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("SeatBookingDetailsID"));
            var CustomerID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID"));
            if (string.IsNullOrEmpty(CustomerID))
            {
                CustomerID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID"));
                SeatBookingDetailsID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("SeatBookingDetailsID"));
            }
            else
            {
                Message = "Session Expired";
            }
            if (Message == "")
            {
                try
                {
                    DateTime selectedDateTo = Convert.ToDateTime(selectedDate.ToString("dd-MM-yyyy") + " 23:59:59");
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("selectedDateFrom", selectedDate);
                        Parameter.Add("selectedDateTo", selectedDate);
                        var Reader = con.ExecuteReader("loadSeatBookingDetailsDatewise", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTSeatBookingDetailsDatewise = new DataTable();
                        DTSeatBookingDetailsDatewise.Load(Reader);
                    }

                    Message = "Success";
                    list = new List<Dictionary<string, object>>();
                    list = convertDatatabletoList(DTSeatBookingDetailsDatewise);
                }
                catch (Exception ex)
                {
                    LogError log = new LogError();
                    log.Error(ex);
                    list = new List<Dictionary<string, object>>();
                    Message = "Error";
                }
            }
            return Tuple.Create(Message, list);
        }
        public Tuple<string> checkSeatCountCartItems()
        {
            string Message = string.Empty;
            try
            {
                Guid CustomerID = Guid.Empty;
                Guid SeatBookingDetailsID = Guid.Empty;
                Guid UniqueBillID = Guid.Empty;
                int cartSelectedcount = 0;

                
                var CustomerIDString = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
                if (!string.IsNullOrEmpty(CustomerIDString) && Guid.TryParse(CustomerIDString, out Guid parsedGuid))
                {
                    CustomerID = parsedGuid;
                }
                else
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message);
                }
                    var SeatBookingDetailsIDString = _httpContextAccessor?.HttpContext?.Session?.GetString("SeatBookingDetailsID");
                if (!string.IsNullOrEmpty(SeatBookingDetailsIDString) && Guid.TryParse(SeatBookingDetailsIDString, out Guid parsedGuidstring))
                {
                    SeatBookingDetailsID = parsedGuidstring;
                }
                else
                {
                    Message = "Session Expired";
                    return Tuple.Create(Message);
                }

                //if (Convert.ToString(HttpContext.Current.Session["CustomerID"]).Trim() != "")
                //{
                //    CustomerID = new Guid(Convert.ToString(HttpContext.Current.Session["CustomerID"]).Trim());
                //    SeatBookingDetailsID = new Guid(Convert.ToString(HttpContext.Current.Session["SeatBookingDetailsID"]).Trim());
                //}
                //else
                //{
                //    Message = "Session Expired";
                //    return Tuple.Create(Message);
                //}
                DataTable DTcountUniquBillidHairCutItemClientSideBillPrimary = new DataTable();
                int seatCount = 0;
                DataSet DS = new DataSet();
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.CommandText = "countHairCutItemClientSideBillPrimaryByUniqueBillID";
                cmd.Parameters.AddWithValue("@SeatBookingDetailsID", SeatBookingDetailsID);
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DS = new DataSet();
                adp.Fill(DS);
                DTcountUniquBillidHairCutItemClientSideBillPrimary = new DataTable();
                DataTable HairCut_Item_Bill_Master = new DataTable();
                DTcountUniquBillidHairCutItemClientSideBillPrimary = DS.Tables[0];
                HairCut_Item_Bill_Master = DS.Tables[1];
                if (DTcountUniquBillidHairCutItemClientSideBillPrimary.Rows.Count > 0)
                {
                    seatCount = Convert.ToInt32(DTcountUniquBillidHairCutItemClientSideBillPrimary.Rows[0]["SeatCount"]);
                }
                cartSelectedcount = HairCut_Item_Bill_Master.Rows.Count;
                if (seatCount != cartSelectedcount)
                {
                    Message = "Cart is Not Full";
                }
                else
                {
                    Message = "Success";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message);
        }
        public Tuple<string> clientSideCheckoutCartItem()
        {
            string Message = string.Empty;
            DataTable DTPrimary = new DataTable();
            try
            {
                Guid UniqueBillID = Guid.Empty;
                var uniqueBillIdString = _httpContextAccessor?.HttpContext?.Session?.GetString("UniqueBillID");
                if (!string.IsNullOrEmpty(uniqueBillIdString) && Guid.TryParse(uniqueBillIdString, out Guid parsedGuid))
                {
                    UniqueBillID = parsedGuid;
                }

                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("UniqueBillID", UniqueBillID);
                    var Reader = con.ExecuteReader("countCustomerUniqueBillIDMasterPrimary", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DTPrimary = new DataTable();
                    DTPrimary.Load(Reader);
                }
                int countUniqueBillID = Convert.ToInt32(DTPrimary.Rows[0]["countUniqueBillID"]);
                if (countUniqueBillID > 0)
                {
                    Message = SelectInsertPrimaryToConstant(UniqueBillID);
                }
                else
                {
                    Message = "Cart Empty";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message);
        }
        private string SelectInsertPrimaryToConstant(Guid UniqueBillID)
        {
            string Message = string.Empty;
            DataTable DtHairCutItemBillMaster = new DataTable();
            try
            {
                //Guid CustomerID = new Guid(Convert.ToString(HttpContext.Current.Session["CustomerID"]).Trim());
                var CustomerID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID"));
                //Guid SeatBookingDetailsID = new Guid(Convert.ToString(HttpContext.Current.Session["SeatBookingDetailsID"]).Trim());
                var SeatBookingDetailsID = Convert.ToString(_httpContextAccessor?.HttpContext?.Session?.GetString("SeatBookingDetailsID"));



                if (!string.IsNullOrEmpty(CustomerID))
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("UniqueBillID", UniqueBillID);
                        Parameter.Add("CustomerID", CustomerID);
                        DtHairCutItemBillMaster = new DataTable();
                        var Reader = con.ExecuteReader("checkCountHairCutItemBillMaster", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DtHairCutItemBillMaster.Load(Reader);
                    }
                    if (DtHairCutItemBillMaster.Rows.Count == 0)
                    {
                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("UniqueBillID", UniqueBillID);
                            con.Execute("SelectInsertPrimaryToConstant", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        }
                        using (con = DBConnection())
                        {
                            var Parameter = new DynamicParameters();
                            Parameter.Add("SeatBookingDetailsID", SeatBookingDetailsID);
                            Parameter.Add("CustomerID", CustomerID);
                            Parameter.Add("BookingStatusMasterID", 2);
                            con.Execute("updateBookingStatus", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        }

                        Message = "Success";
                    }

                    if (DtHairCutItemBillMaster.Rows.Count > 0)
                    {
                        Message = "Already Inserted";
                    }
                }
                else
                {
                    Message = "Session Expired";
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
        static int GenerateSecureRandomNumber()
        {

            try
            {
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    byte[] bytes = new byte[4];
                    rng.GetBytes(bytes);
                    int value = BitConverter.ToInt32(bytes, 0) & int.MaxValue; // Ensure positive number
                    return (value % 900000) + 100000; // Ensures a 6-digit number
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                return 0;
            }
        }
        public (string, int) getBillMasterUniqueBillIDCount(Guid UniqueBillID, Guid CustomerID)
        {
            int Count = 0;
            string Message = string.Empty;
            DataTable Dt = new DataTable();
            try
            {
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("UniqueBillID", UniqueBillID);
                    Parameter.Add("CustomerID", CustomerID);
                    var Reader = con.ExecuteReader("checkCountHairCutItemBillMaster", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    Dt.Load(Reader);
                }
                Count = Dt.Rows.Count;
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Count = 0;
                Message = "Error";
            }
            return (Message, Count);
        }
        public Tuple<string> SendOTPCustomer()
        {
            string Message = string.Empty;
            try
            {
                int Count = 0;
                DataTable DtCustomerRegistration = new DataTable();
                

                string accountSid = _configuration.GetValue<string>("TwiloaccountSid") ?? string.Empty;
                string authToken = _configuration.GetValue<string>("TwiloauthToken") ?? string.Empty;

                int UserOTP = GenerateSecureRandomNumber();
                TwilioClient.Init(accountSid, authToken);
                Guid UniqueBillID = Guid.Empty;

                var varCustomerID = _httpContextAccessor?.HttpContext?.Session?.GetString("CustomerID");
                Guid CustomerID = Guid.Parse(varCustomerID!);
                //if (HttpContext.Current.Session["UniqueBillID"] != null)
                if (!string.IsNullOrEmpty(_httpContextAccessor?.HttpContext?.Session?.GetString("UniqueBillID")))
                {
                    var varUniqueBillID = _httpContextAccessor?.HttpContext?.Session?.GetString("UniqueBillID");
                    UniqueBillID = Guid.Parse(varUniqueBillID!);
                }
                else
                {
                    DataTable DTCustomerMasterPrimary = new DataTable();
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("getClientBillMasterByCustomerID", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTCustomerMasterPrimary.Load(Reader);
                    }
                    if (Guid.TryParse(DTCustomerMasterPrimary.Rows[0]["UniqueBillID"]?.ToString(),out UniqueBillID))
                    {
                        
                    }
                    
                }
                var Result = getBillMasterUniqueBillIDCount(UniqueBillID, CustomerID);
                Message = Result.Item1;
                Count = Result.Item2;
                if (Message == "Success" && Count == 0)
                {
                    clientSideCheckoutCartItem();
                    string CustomerMobileNumber = string.Empty;
                    string CustomerAddress = string.Empty;
                    string FullTiming = string.Empty;
                    string StoreMobileNumber = string.Empty;
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("CustomerID", CustomerID);
                        Parameter.Add("UniqueBillID", UniqueBillID);
                        var Reader = con.ExecuteReader("getCustomerBookingInformation", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DtCustomerRegistration = new DataTable();
                        DtCustomerRegistration.Load(Reader);
                    }
                    string ItemName = string.Empty;

                    string amPmTime = string.Empty;
                    int Bookingcount = 0;
                    if (DtCustomerRegistration.Rows.Count > 0)
                    {
                        var mobile = DtCustomerRegistration.Rows[0]["MobileNo"]?.ToString();
                        CustomerMobileNumber = !string.IsNullOrWhiteSpace(mobile) ? "+91" + mobile.Trim() : string.Empty;
                        var varCustomerAddress = DtCustomerRegistration.Rows[0]["GeneralAddress"]?.ToString();
                        CustomerAddress = !string.IsNullOrWhiteSpace(varCustomerAddress) ? varCustomerAddress : string.Empty;

                        var varItemName = DtCustomerRegistration.Rows[0]["ItemName"]?.ToString();
                        ItemName = !string.IsNullOrWhiteSpace(varItemName) ? varItemName : string.Empty;
                        string BookingDate = string.Empty;
                        for (int i = 0; i < DtCustomerRegistration.Rows.Count; i++)
                        {
                            if (!string.IsNullOrEmpty(FullTiming))
                            {
                                DateTime time = DateTime.ParseExact(DtCustomerRegistration.Rows[i]["FullTiming"].ToString(), "HH:mm:ss", null);
                                //amPmTime = time.ToString("dd/MMM/yyyy hh:mm tt");
                                amPmTime = time.ToString("hh:mm tt") + " to " + time.AddMinutes(30).ToString("hh:mm tt");
                                FullTiming = FullTiming + ", " + amPmTime;
                            }
                            else
                            {
                                DateTime time = DateTime.ParseExact(DtCustomerRegistration.Rows[i]["FullTiming"].ToString(), "HH:mm:ss", null);
                                amPmTime = time.ToString("hh:mm tt") + " to " + time.AddMinutes(30).ToString("hh:mm tt");
                                FullTiming = amPmTime;
                            }
                        }
                        Bookingcount = DtCustomerRegistration.Rows.Count;
                        //DateTime timeDate = DateTime.ParseExact(DtCustomerRegistration.Rows[0]["BookingDate"].ToString(), "yyyy-MM-dd", null);
                        BookingDate = Convert.ToDateTime(DtCustomerRegistration.Rows[0]["BookingDate"]).ToString("dd/MMM/yyyy");
                        FullTiming = BookingDate + " " + FullTiming;
                    }
                    //CustomerMobileNumber = "+919789373230";
                    StoreMobileNumber = "+919865874416";

                    var phone = PhoneNumberResource.Fetch(pathPhoneNumber: CustomerMobileNumber);
                    if (phone != null)
                    {
                        if (phone.Valid == true)
                        {
                            Message = "Valid Phone number";
                        }
                    }
                    if (Message == "Valid Phone number")
                    {
                        try
                        {
                            //var message = MessageResource.Create(
                            //     body: "Person Count: " + Bookingcount + ", booking from : " + CustomerMobileNumber + ", Time : " + FullTiming + ", Name: " + CustomerAddress + ", Item: " + ItemName,
                            //     from: "INFART",
                            //     to: StoreMobileNumber
                            // );

                            Message = "Success";
                        }
                        catch (Exception ex)
                        {
                            LogError log = new LogError();
                            log.Error(ex);
                            Message = "Success";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Success";
            }

            return Tuple.Create(Message);
        }
        public Tuple<string> validateCustomerOTP(string UserOTP)
        {
            string Message = string.Empty;
            string SentOTP = string.Empty;
            try
            {
                DataTable DtMasterPrimary = new DataTable();
                Guid UniqueBillID = getSessionvalueUniqueBillID();
                using (con = DBConnection())
                {
                    var Parameter = new DynamicParameters();
                    Parameter.Add("UserOTP", UserOTP);
                    Parameter.Add("UniqueBillID", UniqueBillID);
                    var Reader = con.ExecuteReader("validateCustomerOTP", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                    DtMasterPrimary.Load(Reader);
                    if (DtMasterPrimary.Rows.Count > 0)
                    {
                        SentOTP = Convert.ToString(DtMasterPrimary.Rows[0]["UserOTP"]);
                    }

                    if (SentOTP == UserOTP)
                    {
                        clientSideCheckoutCartItem();
                        Message = "Success";
                    }
                    else
                    {
                        Message = "Fail";
                    }
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message);
        }
        public Tuple<string, string> checkCustomerOTPSentOrNot()
        {
            string Message = string.Empty;
            string MobileNo = string.Empty;
            try
            {
                Guid UniqueBillID = Guid.Empty;
                Guid CustomerID = Guid.Empty;

                if (getSessionvalueUniqueBillID() != Guid.Empty && getSessionvalueCustomerID() != Guid.Empty)
                {
                    UniqueBillID = getSessionvalueUniqueBillID();
                    CustomerID = getSessionvalueCustomerID();
                    DataTable DTMasterPrimary = new DataTable();
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("UniqueBillID", UniqueBillID);
                        Parameter.Add("CustomerID", CustomerID);
                        var Reader = con.ExecuteReader("checkCustomerOTPSentOrNot", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DTMasterPrimary.Load(Reader);
                        MobileNo = Convert.ToString(DTMasterPrimary.Rows[0]["MobileNo"]);
                        if (string.IsNullOrEmpty(DTMasterPrimary.Rows[0]["UserOTP"].ToString()))
                        {
                            SendOTPCustomer();
                            Message = "Success";
                        }
                        else
                        {
                            Message = "Already OTP Sent";
                        }
                    }
                }
                else
                {
                    Message = "Session Expired";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Tuple.Create(Message, MobileNo);
        }
        public async Task<string> sendUPIPaymentRequest()
        {
            string Message = string.Empty;
            try
            {
                string apiKey = "your_api_key";
                string apiSecret = "your_api_secret";
                string url = "https://api.razorpay.com/v1/payment_links";

                var requestBody = new
                {
                    amount = 10000, // Amount in paisa (10000 = ₹100)
                    currency = "INR",
                    accept_partial = false,
                    description = "Payment request",
                    customer = new
                    {
                        name = "John Doe",
                        email = "johndoe@example.com",
                        contact = "9789373230"
                    },
                    upi_link = true // UPI collect request
                };

                using (HttpClient client = new HttpClient())
                {
                    var byteArray = Encoding.ASCII.GetBytes($"{apiKey}:{apiSecret}");
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                    var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(url, content);

                    string result = await response.Content.ReadAsStringAsync();

                }
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            return Message;
        }
    }
}
