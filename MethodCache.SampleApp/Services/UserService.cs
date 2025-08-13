using MethodCache.SampleApp.Interfaces;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Services
{
    public class UserService : IUserService
    {
        private readonly List<User> _users;
        private readonly Random _random = new();

        public UserService()
        {
            _users = GenerateSampleUsers();
        }

        public async Task<User?> GetUserByIdAsync(int userId)
        {
            // Simulate database delay
            await Task.Delay(_random.Next(50, 200));
            
            Console.WriteLine($"[UserService] Fetching user {userId} from database...");
            return _users.FirstOrDefault(u => u.Id == userId);
        }

        public async Task<User?> GetUserByEmailAsync(string email)
        {
            // Simulate database delay
            await Task.Delay(_random.Next(100, 300));
            
            Console.WriteLine($"[UserService] Searching for user with email {email} in database...");
            return _users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<UserProfile> GetUserProfileAsync(int userId)
        {
            // Simulate expensive profile building operation
            await Task.Delay(_random.Next(200, 500));
            
            Console.WriteLine($"[UserService] Building comprehensive profile for user {userId}...");
            
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
                return new UserProfile { UserId = userId, DisplayName = "Unknown User" };

            return new UserProfile
            {
                UserId = user.Id,
                DisplayName = user.Name,
                Email = user.Email,
                JoinDate = user.CreatedAt,
                LastLogin = DateTime.UtcNow.AddHours(-_random.Next(1, 48)),
                OrderCount = _random.Next(0, 50),
                TotalSpent = _random.Next(100, 5000),
                PreferredCategories = new[] { "Electronics", "Books", "Clothing" }.OrderBy(_ => _random.Next()).Take(_random.Next(1, 4)).ToList(),
                LoyaltyTier = (LoyaltyTier)_random.Next(0, 4),
                IsActive = _random.NextDouble() > 0.1
            };
        }

        public async Task<List<User>> SearchUsersAsync(UserSearchCriteria criteria)
        {
            // Simulate complex search operation
            await Task.Delay(_random.Next(300, 800));
            
            Console.WriteLine($"[UserService] Performing complex user search with criteria: {criteria.CacheKeyPart}...");
            
            var query = _users.AsQueryable();

            if (!string.IsNullOrEmpty(criteria.Name))
                query = query.Where(u => u.Name.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(criteria.Email))
                query = query.Where(u => u.Email.Contains(criteria.Email, StringComparison.OrdinalIgnoreCase));

            if (criteria.IsActive.HasValue)
                query = query.Where(u => u.IsActive == criteria.IsActive.Value);

            if (criteria.CreatedAfter.HasValue)
                query = query.Where(u => u.CreatedAt >= criteria.CreatedAfter.Value);

            var results = query.Skip(criteria.Skip).Take(criteria.Take).ToList();
            
            Console.WriteLine($"[UserService] Search returned {results.Count} users");
            return results;
        }

        public async Task<User> CreateUserAsync(string name, string email)
        {
            // Simulate user creation
            await Task.Delay(_random.Next(200, 400));
            
            Console.WriteLine($"[UserService] Creating new user: {name} ({email})");
            
            var newUser = new User
            {
                Id = _users.Max(u => u.Id) + 1,
                Name = name,
                Email = email,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };
            
            _users.Add(newUser);
            return newUser;
        }

        public async Task<User> UpdateUserAsync(int userId, string name, string email)
        {
            // Simulate user update
            await Task.Delay(_random.Next(150, 300));
            
            Console.WriteLine($"[UserService] Updating user {userId}: {name} ({email})");
            
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
                throw new ArgumentException($"User {userId} not found");

            user.Name = name;
            user.Email = email;
            user.UpdatedAt = DateTime.UtcNow;
            
            return user;
        }

        public async Task DeleteUserAsync(int userId)
        {
            // Simulate user deletion
            await Task.Delay(_random.Next(100, 250));
            
            Console.WriteLine($"[UserService] Deleting user {userId}");
            
            var user = _users.FirstOrDefault(u => u.Id == userId);
            if (user != null)
            {
                _users.Remove(user);
            }
        }

        public string GetActiveUserCount()
        {
            // Simulate quick count operation
            Thread.Sleep(_random.Next(10, 50));
            
            var count = _users.Count(u => u.IsActive);
            Console.WriteLine($"[UserService] Active user count: {count}");
            return count.ToString();
        }
        
        // Additional interface methods
        public async Task<User> GetUserAsync(int userId)
        {
            var user = await GetUserByIdAsync(userId);
            return user ?? new User();
        }
        
        public async Task<User> GetUserWithGroupAsync(int userId)
        {
            var user = await GetUserByIdAsync(userId);
            return user ?? new User();
        }
        
        public User GetUser(int userId)
        {
            Thread.Sleep(_random.Next(50, 200));
            Console.WriteLine($"[UserService] Synchronously fetching user {userId}...");
            return _users.FirstOrDefault(u => u.Id == userId) ?? new User();
        }
        
        public async Task UpdateUserAsync(int userId, User user)
        {
            // Simulate user update with User object
            await Task.Delay(_random.Next(150, 300));
            
            Console.WriteLine($"[UserService] Updating user {userId} with User object");
            
            var existingUser = _users.FirstOrDefault(u => u.Id == userId);
            if (existingUser == null)
                throw new ArgumentException($"User {userId} not found");

            existingUser.Name = user.Name;
            existingUser.Email = user.Email;
            existingUser.UpdatedAt = DateTime.UtcNow;
        }
        
        public async Task LogUserActivityAsync(int userId, string activity)
        {
            await Task.Delay(_random.Next(50, 150));
            Console.WriteLine($"[UserService] Logging activity for user {userId}: {activity}");
        }
        
        public void RecordUserVisit(int userId)
        {
            Thread.Sleep(_random.Next(10, 50));
            Console.WriteLine($"[UserService] Recording visit for user {userId}");
        }
        
        public async Task<User> GetUserByEmailAndTenantAsync(string email, int tenantId)
        {
            await Task.Delay(_random.Next(100, 250));
            Console.WriteLine($"[UserService] Finding user by email {email} in tenant {tenantId}...");
            return _users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) ?? new User();
        }

        private List<User> GenerateSampleUsers()
        {
            var users = new List<User>();
            var names = new[] { "John Doe", "Jane Smith", "Bob Johnson", "Alice Brown", "Charlie Wilson", "Diana Davis", "Eve Miller", "Frank Garcia", "Grace Lee", "Henry Clark" };
            
            for (int i = 1; i <= 100; i++)
            {
                users.Add(new User
                {
                    Id = i,
                    Name = names[_random.Next(names.Length)] + $" {i}",
                    Email = $"user{i}@example.com",
                    CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 365)),
                    IsActive = _random.NextDouble() > 0.1
                });
            }
            
            return users;
        }
    }
}
