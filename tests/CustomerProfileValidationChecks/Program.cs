using InfinityHairartsAPI.Controllers;
using InfinityHairartsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using static InfinityHairartsAPI.Modals.LoginModal;

// No database or live API is used. Invalid requests must stop before the service runs.
string?[] invalid = [
    null, "", "   ", "ganeshcse.krct@gmail.com6577", "ganesh@gmail", "ganesh@gmail.c",
    "ganeshcse.krct@gmail.comc", "ganesh@yahoo.com", "ganesh@outlook.com", "ganesh@example.com",
    "ganesh@googlemail.com", "ganesh@sub.gmail.com", "ganesh@gmail.com.example.com",
    "ganesh@@gmail.com", "ganesh@gmail..com", "ganesh@-gmail.com", "ganesh@gmail-.com",
    ".ganesh@gmail.com", "ganesh.@gmail.com", "ga..nesh@gmail.com", "ga nesh@gmail.com",
    new string('a', 65) + "@gmail.com", "ganesh@" + new string('a', 64) + ".com",
    new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + ".example.com"
];
string[] valid = [
    "ganeshcse.krct@gmail.com", "ganesh+booking@gmail.com", "Ganesh@GMAIL.COM",
    "ganesh_123@gmail.com", "o'connor@gmail.com",
    new string('a', 64) + "@gmail.com", " ganesh@gmail.com "
];

var controller = new LoginController(null!);
var service = new LoginService(null!, null!, null!);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

foreach (var email in invalid)
{
    Check(!CustomerEmailAddress.IsValid(email), $"Reject invalid email: {email}");
    var request = new UpdateCustomerProfileRequest { EmailAddress = email };
    Check(controller.UpdateCustomerRegistration(request) is BadRequestObjectResult,
        $"Return HTTP 400 before accessing the service: {email}");
    Check(service.updateCustomerRegistration(request).StartsWith("Enter a valid Gmail address"),
        $"Reject email before accessing a session or database: {email}");
}

foreach (var email in valid)
    Check(CustomerEmailAddress.IsValid(email), $"Accept valid email: {email}");

Console.WriteLine($"PASS: {checks} customer email validation checks.");
