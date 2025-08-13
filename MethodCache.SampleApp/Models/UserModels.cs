using MethodCache.Core;

namespace MethodCache.SampleApp.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsActive { get; set; }
        public int TenantId { get; set; }
    }

    public class UserProfile
    {
        public int UserId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime JoinDate { get; set; }
        public DateTime LastLogin { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalSpent { get; set; }
        public List<string> PreferredCategories { get; set; } = new();
        public LoyaltyTier LoyaltyTier { get; set; }
        public bool IsActive { get; set; }
        public string ProfilePictureUrl { get; set; } = string.Empty;
        public Dictionary<string, string> Preferences { get; set; } = new();
        public DateTime LastLoginAt { get; set; }
    }

    public class UserSearchCriteria : ICacheKeyProvider
    {
        public string? Name { get; set; }
        public string? NameFilter { get; set; }
        public string? Email { get; set; }
        public string? EmailFilter { get; set; }
        public bool? IsActive { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public int? TenantId { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 10;

        // Custom cache key generation to ensure proper cache behavior
        public string CacheKeyPart => 
            $"name:{Name ?? NameFilter ?? "null"}_email:{Email ?? EmailFilter ?? "null"}_active:{IsActive?.ToString() ?? "null"}_createdAfter:{CreatedAfter?.ToString("yyyy-MM-dd") ?? "null"}_tenant:{TenantId?.ToString() ?? "null"}_skip:{Skip}_take:{Take}";
    }

    public enum LoyaltyTier
    {
        Bronze = 0,
        Silver = 1,
        Gold = 2,
        Platinum = 3
    }
}