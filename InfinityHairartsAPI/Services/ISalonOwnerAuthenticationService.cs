using InfinityHairartsAPI.Modals;

namespace InfinityHairartsAPI.Services;

public interface ISalonOwnerAuthenticationService
{
    Task<SalonOwnerLoginResponse?> AuthenticateAsync(SalonOwnerLoginRequest request, CancellationToken cancellationToken);
    Task<SalonOwnerLoginResponse?> GetActiveSessionOwnerAsync(Guid salonOwnerId, CancellationToken cancellationToken);
    Task<SalonOwnerLoginResponse> InitializeAsync(InitializeSalonOwnerRequest request, CancellationToken cancellationToken);
}
