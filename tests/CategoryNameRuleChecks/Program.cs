using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SalonexaAdminAPI.Modals;
using SalonexaAdminAPI.Services;

// Pure checks of the public-text content rules; no database needed.
var blocked = new[]
{
    "Massage", "Body Massage", "m a s s a g e", "M@ssage", "maassaage", "BodyMassage", "Thai massages", "masseuse",
    "Porn", "p0rn", "Sex", "Sexy Services", "Escort", "Nude", "XXX", "happy ending", "body to body",
    "Fuck", "Shit", "b1tch", "Asshole", "madarchod",
    "Drugs", "Weed", "Cocaine", "Weapons", "Gambling", "Bomb",
    "Spa", "Hair Spa", "HairSpa", "Body Spa", "SPA services", "Idiot", "Rapist", "Pedophile", "Dickhead", "punda", "thevidiya",
    // Disguised spellings
    "s.e.x", "s-e-x", "s e x", "seex", "SEEEX", "massaaage", "mas​sage", "s​ex", "ｓｅｘ", "mаssage", "ѕex",
    "p.o.r.n", "Sp@", "Hair $pa", "F u c k",
};
var allowed = new[]
{
    "Hair Cut", "Hair", "Beard", "Facial", "Skin", "Colour", "Nails", "Body", "Other", "Adult Hair Cut", "Kids Cut",
    "Bridal Makeup", "Cocktail Party Styling", "Class Of 2026", "Scunthorpe Special", "Passage Braids", "Message Braids",
    "Cornrows", "Thorn Tattoo Art", "Space Buns", "Sparkle Highlights", "Chutney Colour", "Raccoon Tail Braids", "Cumin Henna",
    "Keratin Treatment", "Café Style", "Hair & Makeup", "Bobs and Layers", "Bob Cut", "Wed Ready Look", "Classic Gents",
    "A B C Hair Studio", "Glow Studio & Salon", "Hair 100 Off", "Men's Fade", "Hair Wash & Blow-dry", "Threading (Eyebrows)",
    "Tamil Nadu Bridal", "தமிழ் சலூன்", "", "   ",
};

var failures = new List<string>();
foreach (var name in blocked)
    if (!PublicTextRules.IsBlocked(name)) failures.Add($"should be blocked: '{name}'");
foreach (var name in allowed)
    if (PublicTextRules.IsBlocked(name)) failures.Add($"should be allowed: '{name}'");

// The service writer must refuse a restricted service name or description before it touches the database,
// so a restricted word cannot be slipped in under an innocent category.
var configuration = new ConfigurationBuilder().Build();
var writer = new SalonOwnerServicesWriter(configuration, null!, new SalonOwnerServicesReader(configuration),
    NullLogger<SalonOwnerServicesWriter>.Instance);
async Task ExpectRefused(string label, SalonOwnerServiceRequest request)
{
    try
    {
        await writer.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), request, CancellationToken.None);
        failures.Add($"writer accepted {label}");
    }
    catch (ArgumentException exception) when (exception.Message.Contains("not allowed")) { }
    catch (Exception exception) { failures.Add($"writer failed on {label} for the wrong reason: {exception.GetType().Name}"); }
}
SalonOwnerServiceRequest Request(string name = "Haircut", string category = "Hair Cut", string? description = "Neat trim") => new()
{
    ServiceName = name, Category = category, Description = description, Price = 100m, DurationMinutes = 30, IsActive = true,
};
await ExpectRefused("a restricted service name", Request(name: "Body Massage"));
await ExpectRefused("a restricted category", Request(category: "Spa"));
await ExpectRefused("a restricted description", Request(description: "Relaxing m@ssage included"));
await ExpectRefused("a disguised service name", Request(name: "s.e.x"));

if (failures.Count > 0)
{
    failures.ForEach(Console.Error.WriteLine);
    Environment.ExitCode = 1;
}
else Console.WriteLine($"Public text rule checks passed ({blocked.Length} blocked, {allowed.Length} allowed, writer refusals verified).");
