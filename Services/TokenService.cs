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

    public string Create(User u)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),     // <= важно для Clients.User(...)
            new Claim(JwtRegisteredClaimNames.UniqueName, u.UserName),
            new Claim(JwtRegisteredClaimNames.Email, u.Email)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_o.ExpiresMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
