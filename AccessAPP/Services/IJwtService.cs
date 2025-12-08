using AccessAPP.Models;
using System.Security.Claims;

namespace AccessAPP.Services
{
    public interface IJwtService
    {
        string GenerateToken(User user);
        ClaimsPrincipal? ValidateToken(string token);
    }
}
