using AccessAPP.Models;
using static AccessAPP.Models.AuthModels;

namespace AccessAPP.Services
{
    public interface IUserService
    {
        Task<User?> AuthenticateAsync(string email, string password);
        Task<User> RegisterAsync(RegisterRequest request);
        Task<User?> GetUserByIdAsync(string id);
        Task<User?> GetUserByEmailAsync(string email);
    }
}