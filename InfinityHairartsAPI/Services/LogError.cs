namespace InfinityHairartsAPI.Services
{
    public class LogError
    {
        public string Error(Exception ex)
        {
            DailyFileLogWriter.Write(
                LogLevel.Error,
                "InfinityHairartsAPI.HandledException",
                default,
                "A handled API service exception occurred.",
                ex);
            return "";
        }
    }
}
