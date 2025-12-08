namespace AccessAPP.Models
{
    public class AuthModels
    {
        public class LoginRequest
        {
            public required string Email { get; set; }
            public required string Password { get; set; }
        }
        public class RegisterRequest
        {
            public required string Email { get; set; }
            public required string Password { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
        }
        public class AuthResponse
        {
            public required string Token { get; set; }
            public required DateTime ExpiresAt { get; set; }
            public required UserInfo User { get; set; }
        }
        public class UserInfo
        {
            public required string Id { get; set; }
            public required string Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public List<string> Roles { get; set; } = new();
        }
    }
}
