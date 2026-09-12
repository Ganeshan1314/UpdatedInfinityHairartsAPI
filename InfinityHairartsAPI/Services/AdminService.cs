using Dapper;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace InfinityHairartsAPI.Services
{
    public class AdminService
    {
        SqlConnection con;
        List<Dictionary<string, object>> listUserRegistration = new List<Dictionary<string, object>>();
        Dictionary<string, object> dictUserRegistration = new Dictionary<string, object>();
        DataTable DTUserRegistration = new DataTable();
        //AWSCommonClass CommonClass = new AWSCommonClass();
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        public AdminService(IHttpContextAccessor httpContextAccessor, IConfiguration configuration, IWebHostEnvironment env)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _env = env;
        }
        public SqlConnection DBConnection()
        {
            //con = new SqlConnection(ConfigurationManager.ConnectionStrings["dbconnection"].ConnectionString);
            con = new SqlConnection(_configuration.GetConnectionString("dbconnection"));
            con.Open();
            return con;
        }
        public string InsertNewHairCutTpyes(string HairCutItemName, string HairCutItemPrice, IFormFile  imgprofilephoto)
        {
            try
            {
                Guid UniqueCustID = Guid.Empty;
                string SessionMessage = string.Empty;
                SessionMessage = SessionExpiredOrNot();
                if (SessionMessage == "Session Expired")
                {
                    return "Session Expired";
                }
                Guid UniqueItemID = Guid.NewGuid();
                int CountItemName = 0;
                DataSet DS = new DataSet();
                HairCutItemName = HairCutItemName.Trim();
                HairCutItemPrice = HairCutItemPrice.Trim();
                string AwsUpdateStatus = string.Empty;
                string LocalFilePath = string.Empty;
                //string Path1234 = System.Web.HttpContext.Current.Server.MapPath("~/UserUploads/HairCutImages/");
                string Path1234 = Path.Combine(_env.WebRootPath, "UserUploads", "HairCutImages");
                string filename = Path.GetFileName(imgprofilephoto.FileName);
                LocalFilePath = Path1234 + filename;
                string fileExtension = Path.GetExtension(imgprofilephoto.FileName);
                filename = UniqueItemID.ToString() + fileExtension;
                //imgprofilephoto.SaveAs(Path1234 + filename);
                LocalFilePath = Path1234 + filename;
                string AWSFilePath = "EmployeeProfilePicture/InfinityHairArts/" + UniqueItemID + "/" + filename;
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.CommandText = "CheckItemNameExist";
                cmd.Parameters.AddWithValue("@ItemName", HairCutItemName);
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DS = new DataSet();
                adp.Fill(DS);
                CountItemName = DS.Tables[0].Rows.Count;
                if (CountItemName > 0)
                {
                    return "ItemName Already Exist";
                }
                else
                {
                    if (LocalFilePath != "")
                    {
                        //AwsUpdateStatus = CommonClass.UpdateFilesAWS(LocalFilePath, AWSFilePath, UniqueItemID.ToString());
                    }
                    else
                    {
                        AwsUpdateStatus = "";
                    }
                    if (AwsUpdateStatus == "Success")
                    {
                        using (con = DBConnection())
                        {
                            UniqueCustID = new Guid(_httpContextAccessor.HttpContext!.Session.GetString("UniqueCustID")!);
                            var Parameter = new DynamicParameters();
                            Parameter.Add("@UniqueCustID", UniqueItemID);
                            Parameter.Add("@HairCutItemName", HairCutItemName);
                            Parameter.Add("@HairCutItemPrice", HairCutItemPrice);
                            Parameter.Add("@ItemImageName", filename);
                            Parameter.Add("@s3Filepath", AWSFilePath);
                            Parameter.Add("@CreatedDate", DateTime.Now);
                            Parameter.Add("@CreatedBy", UniqueCustID);
                            Parameter.Add("@IsDelete", 0);
                            con.Execute("InsertNewHairCutTpyes", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                            return "Success";
                        }
                    }
                    else
                    {
                        return "Error";
                    }
                }

            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                return "Error";
            }
        }
        public string UpdateNewHairCutTpyes(Guid UniqueItemID, string HairCutItemName, string HairCutItemPrice, IFormFile imgprofilephoto)
        {
            string ReturnMessage = string.Empty;
            try
            {
                Guid UniqueCustID = Guid.Empty;
                string SessionMessage = string.Empty;
                SessionMessage = SessionExpiredOrNot();
                if (SessionMessage == "Session Expired")
                {
                    return "Session Expired";
                }
                int CountItemName = 0;
                DataSet DS = new DataSet();
                HairCutItemName = HairCutItemName.Trim();
                HairCutItemPrice = HairCutItemPrice.Trim();
                string AwsUpdateStatus = string.Empty;
                string LocalFilePath = string.Empty;
                string ItemImageName = string.Empty;
                Guid ItemID = Guid.Empty;
                string filename = string.Empty;
                string fileExtension = string.Empty;
                string AWSFilePath = string.Empty;
                string DeleteFilePath = string.Empty;
                //string LocalPath = System.Web.HttpContext.Current.Server.MapPath("~/UserUploads/HairCutImages/");
                string LocalPath = Path.Combine(_env.WebRootPath, "UserUploads", "HairCutImages"); ;
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.CommandText = "CheckItemNameExist";
                cmd.Parameters.AddWithValue("@ItemName", HairCutItemName);
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DS = new DataSet();
                adp.Fill(DS);
                CountItemName = DS.Tables[0].Rows.Count;
                string DeleteFileName = string.Empty;
                if (CountItemName > 0)
                {
                    ItemID = new Guid(DS.Tables[0].Rows[0]["ItemID"].ToString());
                    ItemImageName = Convert.ToString(DS.Tables[0].Rows[0]["ItemImageName"]).Trim();
                    AWSFilePath = Convert.ToString(DS.Tables[0].Rows[0]["ItemImageS3FilePath"]).Trim();
                }
                if (CountItemName == 0)
                {
                    using (con = DBConnection())
                    {
                        var Parameter = new DynamicParameters();
                        Parameter.Add("ItemID", UniqueItemID);
                        var Reader = con.ExecuteReader("getHairCutItemDetails", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        DataTable DT = new DataTable();
                        DT.Load(Reader);
                        ItemImageName = Convert.ToString(DT.Rows[0]["ItemImageName"]);
                        AWSFilePath = Convert.ToString(DT.Rows[0]["ItemImageS3FilePath"]);
                    }
                }
                if (imgprofilephoto != null)
                {
                    filename = Path.GetFileName(imgprofilephoto.FileName);
                    fileExtension = Path.GetExtension(imgprofilephoto.FileName);
                    filename = ItemImageName;
                    LocalFilePath = LocalPath + filename;
                    DeleteFilePath = LocalPath + ItemImageName;
                    if (File.Exists(DeleteFilePath))
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        File.Delete(DeleteFilePath);
                    }
                    //imgprofilephoto.SaveAs(LocalFilePath);
                    AWSFilePath = "EmployeeProfilePicture/InfinityHairArts/" + UniqueItemID + "/" + filename;
                }
                if (CountItemName > 1)
                {
                    ReturnMessage = "ItemName Already Exist";
                }
                else if (CountItemName == 0 || (CountItemName == 1 && ItemID == UniqueItemID))
                {
                    if (LocalFilePath != "")
                    {
                        //AwsUpdateStatus = CommonClass.UpdateFilesAWS(LocalFilePath, AWSFilePath, UniqueItemID.ToString());
                    }
                    else
                    {
                        AwsUpdateStatus = "Success";
                    }
                    if (AwsUpdateStatus == "Success")
                    {
                        using (con = DBConnection())
                        {
                            UniqueCustID = new Guid(_httpContextAccessor.HttpContext!.Session.GetString("UniqueCustID")!);
                            var Parameter = new DynamicParameters();
                            Parameter.Add("@UniqueCustID", UniqueItemID);
                            Parameter.Add("@HairCutItemName", HairCutItemName);
                            Parameter.Add("@HairCutItemPrice", HairCutItemPrice);
                            if (filename.Trim() != "")
                            {
                                Parameter.Add("@ItemImageName", filename);
                                Parameter.Add("@s3Filepath", AWSFilePath);
                            }
                            else
                            {
                                Parameter.Add("@ItemImageName", ItemImageName);
                                Parameter.Add("@s3Filepath", AWSFilePath);
                            }
                            Parameter.Add("@CreatedDate", DateTime.Now);
                            Parameter.Add("@CreatedBy", UniqueCustID);
                            Parameter.Add("@IsDelete", 0);
                            con.Execute("UpdateHairCutTpyes", Parameter, commandType: CommandType.StoredProcedure, commandTimeout: 0);
                        }
                    }
                    ReturnMessage = "Success";
                }
                else
                {
                    ReturnMessage = "ItemName Already Exist";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                ReturnMessage = "Error";
            }
            return ReturnMessage;
        }
        public Tuple<string> deleteHairCutItemDetails(Guid UniqueItemID, string ImageName)
        {
            string ReturnMessage = string.Empty;
            try
            {
                Guid UniqueCustID = Guid.Empty;
                string SessionMessage = string.Empty;
                SessionMessage = SessionExpiredOrNot();
                if (SessionMessage == "Session Expired")
                {
                    ReturnMessage = "Session Expired";
                }
                DataSet DS = new DataSet();
                string AwsUpdateStatus = string.Empty;
                string LocalFilePath = string.Empty;
                string ItemImageName = string.Empty;
                Guid ItemID = Guid.Empty;
                string filename = ImageName;
                string AWSFilePath = string.Empty;
                string DeleteFilePath = string.Empty;
                //string LocalPath = System.Web.HttpContext.Current.Server.MapPath("~/UserUploads/HairCutImages/");
                string LocalPath = Path.Combine(_env.WebRootPath, "UserUploads", "HairCutImages");
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.Parameters.AddWithValue("UniqueItemID", UniqueItemID);
                cmd.Parameters.AddWithValue("DeletedDate", DateTime.Now);
                cmd.Parameters.AddWithValue("DeletedBy", new Guid(_httpContextAccessor.HttpContext!.Session.GetString("UniqueCustID")!));
                cmd.CommandText = "deleteHairCutItem";
                cmd.ExecuteNonQuery();
                AWSFilePath = "EmployeeProfilePicture/InfinityHairArts/" + UniqueItemID + "/" + filename;
                DeleteFilePath = LocalPath + filename;
                if (File.Exists(DeleteFilePath))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    File.Delete(DeleteFilePath);
                }
                //AwsUpdateStatus = CommonClass.DeleteFilesAWS(AWSFilePath, UniqueItemID.ToString());
                if (AwsUpdateStatus == "Success")
                {
                    ReturnMessage = "Success";
                }
                else
                {
                    ReturnMessage = "Error";
                }
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                ReturnMessage = "Error";
            }
            return Tuple.Create(ReturnMessage);
        }
        public Tuple<string, List<Dictionary<string, object>>> GetListHairCutTpyes()
        {
            List<Dictionary<string, object>> ListGetListHairCutTpyes = new List<Dictionary<string, object>>();
            string Message = string.Empty;
            DataTable DT = new DataTable();
            string ImageS3Url = string.Empty;
            //AWSCommonClass common = new AWSCommonClass();
            try
            {
                Guid UniqueItemID = Guid.NewGuid();
                int CountItemName = 0;
                string AwsUpdateStatus = string.Empty;
                string LocalFilePath = string.Empty;
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.CommandText = "getHairCut_Item_List";
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DT = new DataTable();
                adp.Fill(DT);
                CountItemName = DT.Rows.Count;
                string AWSFolderPath = string.Empty;
                //string ShowCustomerImage = ConfigurationManager.AppSettings["ShowCustomerImage"];
                string ShowCustomerImage = Convert.ToString(_configuration["ShowCustomerImage"]);
                for (int i = 0; i < DT.Rows.Count; i++)
                {
                    AWSFolderPath = ShowCustomerImage + Convert.ToString(DT.Rows[i]["HairCutItemImage"]);
                    DT.Rows[i]["HairCutItemImageS3Path"] = AWSFolderPath;
                }
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            ListGetListHairCutTpyes = returnList(DT);
            return Tuple.Create(Message, ListGetListHairCutTpyes);
        }
        public Tuple<string, List<Dictionary<string, object>>> getHairCutItemDetails(Guid ItemID)
        {
            List<Dictionary<string, object>> ListgetItemDetails = new List<Dictionary<string, object>>();
            string Message = string.Empty;
            DataTable DT = new DataTable();
            try
            {
                string AwsUpdateStatus = string.Empty;
                string LocalFilePath = string.Empty;
                con = DBConnection();
                SqlCommand cmd = new SqlCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = 0;
                cmd.Connection = con;
                cmd.Parameters.AddWithValue("ItemID", ItemID);
                cmd.CommandText = "getHairCutItemDetails";
                SqlDataAdapter adp = new SqlDataAdapter(cmd);
                DT = new DataTable();
                adp.Fill(DT);
                Message = "Success";
            }
            catch (Exception ex)
            {
                LogError log = new LogError();
                log.Error(ex);
                Message = "Error";
            }
            ListgetItemDetails = returnList(DT);
            return Tuple.Create(Message, ListgetItemDetails);
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
        public string SessionExpiredOrNot()
        {
            //if (Convert.ToString(HttpContext.Current.Session["UniqueCustID"]).Trim() != "")
            if (Convert.ToString(_httpContextAccessor.HttpContext!.Session.GetString("UniqueCustID")!) != "")
            {
                return "Session Not Expired";
            }
            else
            {
                return "Session Expired";
            }
        }
    }
}
