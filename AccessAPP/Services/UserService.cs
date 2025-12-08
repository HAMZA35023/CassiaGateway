using AccessAPP.Models;
using static AccessAPP.Models.AuthModels;

namespace AccessAPP.Services
{
    public class UserService: IUserService
    {
        // In-memory user storage for learning purposes
        // In a real app, this would be a database
        private static readonly List<User> _users = new()
        {
            new User
            {
                Id = "1",
                Email = "admin@test.com",
                Password = "password123", // In real app, this would be hashed!
                FirstName = "Admin",
                LastName = "User",
                Roles = new() { "Admin", "User" }
            },
            new User
            {
                Id = "2",
                Email = "user@test.com",
                Password = "password123",
                FirstName = "Regular",
                LastName = "User",
                Roles = new() { "User" }
            }
        };

        public Task<User?> AuthenticateAsync(string email, string password)
        {
            // In a real app, you would hash the password and compare hashes
            var user = _users.FirstOrDefault(u =>
                u.Email.Equals(email, StringComparison.OrdinalIgnoreCase) &&
                u.Password == password);

            return Task.FromResult(user);
        }

        public Task<User> RegisterAsync(RegisterRequest request)
        {
            // Check if user already exists
            if (_users.Any(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("User with this email already exists");
            }

            var user = new User
            {
                Email = request.Email,
                Password = request.Password, // In real app, hash this!
                FirstName = request.FirstName,
                LastName = request.LastName
            };

            _users.Add(user);
            return Task.FromResult(user);
        }

        public Task<User?> GetUserByIdAsync(string id)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            return Task.FromResult(user);
        }

        public Task<User?> GetUserByEmailAsync(string email)
        {
            var user = _users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(user);
        }
    }
}

