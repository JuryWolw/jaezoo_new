using JaeZoo.Server.Models.Ads;
using Microsoft.AspNetCore.Http;

namespace JaeZoo.Server.Services.Ads;

public interface IAdsService
{
    Task<AdsResponse> GetPublicAdsAsync(CancellationToken cancellationToken = default);
    Task<AdminAdsManifestResponse> GetAdminManifestAsync(CancellationToken cancellationToken = default);
    Task<AdminAdsManifestResponse> SaveManifestAsync(SaveAdsManifestRequest request, CancellationToken cancellationToken = default);
    Task<UploadAdImageResponse> UploadImageAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(string imageKey, CancellationToken cancellationToken = default);
}
