using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using Microsoft.EntityFrameworkCore;
using BC = BCrypt.Net.BCrypt;

namespace KaraokePlatform.Services
{
    public class UserService
    {
        private readonly AppDbContext _context;

        public UserService(AppDbContext context)
        {
            _context = context;
        }

        // Хэширование и создание нового пользователя (для админки)
        public async Task<bool> CreateUserAsync(string username, string password, Role role)
        {
            // Проверяем, нет ли уже пользователя с таким именем
            bool exists = await _context.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower());
            if (exists) return false;

            var user = new AppUser
            {
                Username = username,
                PasswordHash = BC.HashPassword(password), // Хэшируем пароль перед сохранением
                Role = role
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return true;
        }

        // Проверка учетных данных при входе
        public async Task<AppUser?> AuthenticateAsync(string username, string password)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null) return null;

            // Проверяем, совпадает ли введенный пароль с хэшем из базы
            if (!BC.Verify(password, user.PasswordHash)) return null;

            return user;
        }
    }
}