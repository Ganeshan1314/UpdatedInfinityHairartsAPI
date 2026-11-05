using Microsoft.AspNetCore.Identity;

Console.Write("New super admin password (12–256 characters): ");
var password = ReadPassword();
Console.Write("Confirm password: ");
var confirmation = ReadPassword();
if (password.Length is < 12 or > 256 || password != confirmation)
{
    Console.Error.WriteLine("Passwords must match and contain 12–256 characters.");
    return 1;
}
Console.WriteLine("PasswordHash for dbo.SuperAdmin (keep the hash private):");
Console.WriteLine(new PasswordHasher<string>().HashPassword("super-admin", password));
return 0;

static string ReadPassword()
{
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(characters.ToArray()); }
        if (key.Key == ConsoleKey.Backspace) { if (characters.Count > 0) characters.RemoveAt(characters.Count - 1); }
        else if (!char.IsControl(key.KeyChar) && characters.Count < 257) characters.Add(key.KeyChar);
    }
}
