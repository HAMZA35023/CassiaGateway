namespace AccessAPP.Models
{
    public class User
    {
        public string Id { get; set; }
        public required string Email { get; set; }
        public required string Password { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public List<string> Roles { get; set; } = new() { "User"};
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
