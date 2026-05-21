using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JaeZoo.Server.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JaeZoo.Server.Services;

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public int ExpiresMinutes { get; set; } = 60;
}

public class TokenService(IOptions<JwtOptions> opts)
{
    private readonly JwtOptions _o = opts.Value;

    public string Create(User u, IEnumerable<GlobalRole>? roles = null, Guid? sessionId = null, DateTime? expiresUtc = null)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),     // <= важно для Clients.User(...)
            new Claim(JwtRegisteredClaimNames.UniqueName, UserIdentityService.GetLogin(u)),
            new Claim("public_id", u.PublicId ?? string.Empty),
            new Claim("display_name", UserIdentityService.GetPublicName(u)),
            new Claim("email_confirmed", u.EmailConfirmed ? "true" : "false"),
            new Claim("token_version", u.TokenVersion.ToString()),
            new Claim("security_stamp", u.SecurityStamp ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Email, u.Email)
        };

        if (sessionId.HasValue)
            claims.Add(new Claim("sid", sessionId.Value.ToString()));

        foreach (var role in (roles ?? Enumerable.Empty<GlobalRole>()).Distinct())
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresUtc ?? DateTime.UtcNow.AddMinutes(_o.ExpiresMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
