using MethodCache.Core;
using MethodCache.SampleApp.Models;
using System.Threading.Tasks;

namespace MethodCache.SampleApp.Interfaces
{
    /// <summary>
    /// User service demonstrating basic caching scenarios
    /// </summary>
    public interface IUserService
    {
        // Basic caching with default settings
        [Cache]
        Task<User> GetUserAsync(int userId);

        // Caching with group name
        [Cache("Users")]
        Task<User> GetUserWithGroupAsync(int userId);

        // Caching with RequireIdempotent flag
        [Cache(RequireIdempotent = true)]
        Task<UserProfile> GetUserProfileAsync(int userId);

        // Synchronous caching
        [Cache]
        User GetUser(int userId);

        // Cache invalidation with specific tags
        [CacheInvalidate(Tags = new[] { "Users", "UserProfile" })]
        Task UpdateUserAsync(int userId, User user);

        // Cache invalidation with single tag
        [CacheInvalidate(Tags = new[] { "Users" })]
        Task DeleteUserAsync(int userId);

        // Method without caching (pass-through)
        Task LogUserActivityAsync(int userId, string activity);

        // Void return type with caching
        [Cache]
        void RecordUserVisit(int userId);

        // Complex parameter types
        [Cache]
        Task<List<User>> SearchUsersAsync(UserSearchCriteria criteria);

        // Multiple parameters
        [Cache]
        Task<User> GetUserByEmailAndTenantAsync(string email, int tenantId);
        
        // Additional method referenced in Program.cs
        [Cache]
        Task<User?> GetUserByIdAsync(int userId);
    }
}
