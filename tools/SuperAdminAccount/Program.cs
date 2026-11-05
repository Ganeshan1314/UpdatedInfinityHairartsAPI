using Dapper;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

if (args.Length != 1 || args[0] is not ("migrate" or "create" or "list"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SuperAdminAccount -- migrate|create|list");
    return 1;
}
var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "InfinityHairartsAPI.sln")))
    root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("Run from the API repository.");
var configuration = new ConfigurationBuilder().SetBasePath(Path.Combine(root, "InfinityHairartsAPI"))
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables().Build();
var settings = new SqlConnectionStringBuilder(configuration.GetConnectionString("dbconnection"));
// This CLI operates on the local workspace database; use the SQL migration for server deployment.
var host = settings.DataSource.Split('\\', ',')[0];
if (host != "." && host != "(localdb)" && host != "127.0.0.1"
    && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("This setup tool only operates on a local SQL Server. Deploy Database/SuperAdminAccounts.sql manually on the server.");
    return 1;
}
// Match the existing local SQL checks while preserving any explicit encryption setting.
if (!settings.ShouldSerialize("Encrypt")) settings.Encrypt = SqlConnectionEncryptOption.Optional;
configuration["ConnectionStrings:dbconnection"] = settings.ConnectionString;
Console.WriteLine($"Database: {settings.InitialCatalog} on {settings.DataSource}");
try
{
    await using var connection = new SqlConnection(settings.ConnectionString);
    if (args[0] == "migrate")
    {
        await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(root, "InfinityHairartsAPI", "Database", "SuperAdminAccounts.sql")));
        Console.WriteLine("dbo.SuperAdmin is ready. No accounts were created or changed.");
    }
    else if (args[0] == "list")
    {
        var rows = await connection.QueryAsync("SELECT UserName, DisplayName, Role, IsActive FROM dbo.SuperAdmin ORDER BY UserName");
        foreach (var row in rows) Console.WriteLine($"{row.UserName} | {row.DisplayName} | {row.Role} | Active: {row.IsActive}");
        if (!rows.Any()) Console.WriteLine("No super-admin accounts have been created yet.");
    }
    else
    {
        Console.Write("Username (3-100 letters, numbers, dot, underscore or hyphen): ");
        var userName = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Display name: ");
        var displayName = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Password (12-256 characters): ");
        var password = ReadPassword();
        Console.Write("Confirm password: ");
        var confirmation = ReadPassword();
        if (password.Length is < 12 or > 256 || string.IsNullOrWhiteSpace(password) || password != confirmation)
        {
            Console.Error.WriteLine("Passwords must match and contain 12-256 nonblank characters.");
            return 1;
        }
        var id = await new SuperAdminAccountStore(configuration).CreateAsync(userName, displayName,
            new PasswordHasher<string>().HashPassword(userName, password), CancellationToken.None);
        Console.WriteLine($"Created super admin '{userName}' (ID: {id}). Sign in at /super-admin/login using the password you entered.");
    }
    return 0;
}
catch (SqlException error) when (error.Number is 2601 or 2627)
{
    Console.Error.WriteLine("That super-admin username already exists. No account was changed.");
    return 1;
}
catch (SqlException error) when (error.Number == 208)
{
    Console.Error.WriteLine("Run this tool with 'migrate' first to create dbo.SuperAdmin.");
    return 1;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(characters.ToArray()); }
        if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) characters.RemoveAt(characters.Count - 1); }
        else if (!char.IsControl(key.KeyChar) && characters.Count < 257) characters.Add(key.KeyChar);
    }
}
