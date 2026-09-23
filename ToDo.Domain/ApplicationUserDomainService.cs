
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Entities.Dto;

namespace ToDo.Domain
{
    public class ApplicationUserDomainService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;

        public ApplicationUserDomainService(
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context)
        {
            _userManager = userManager;
            _context = context;
        }

        #region Logging Methods

        private string FormatUserName(string? userName)
        {
            return $"👤 {userName ?? "未知用户"}";
        }

        private async Task LogUserCreationAsync(ApplicationUser newUser, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.创建,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = newUser.Id,
                TargetName = newUser.UserName,
                BeforeContent = $"{FormatUserName(newUser.UserName)}",
                AfterContent = $"{FormatUserName(newUser.UserName)}\n" +
                             $"真实姓名: {newUser.RealName}\n" +
                             $"邮箱: {newUser.Email}\n" +
                             $"性别: {GetGenderDisplayName(newUser.Gender)}\n" +
                             $"角色: {GetRoleDisplayName(newUser.Role)}\n" +
                             $"状态: {GetStatusDisplayName(newUser.Status)}"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogUserUpdateAsync(ApplicationUser originalUser, ApplicationUser updatedUser, ApplicationUser operatorUser)
        {
            var beforeChanges = new List<string>();
            var afterChanges = new List<string>();

            if (originalUser.RealName != updatedUser.RealName)
            {
                beforeChanges.Add($"真实姓名: {originalUser.RealName}");
                afterChanges.Add($"真实姓名: {updatedUser.RealName}");
            }

            if (originalUser.Email != updatedUser.Email)
            {
                beforeChanges.Add($"邮箱: {originalUser.Email}");
                afterChanges.Add($"邮箱: {updatedUser.Email}");
            }

            if (originalUser.Gender != updatedUser.Gender)
            {
                beforeChanges.Add($"性别: {GetGenderDisplayName(originalUser.Gender)}");
                afterChanges.Add($"性别: {GetGenderDisplayName(updatedUser.Gender)}");
            }

            if (originalUser.PhoneNumber != updatedUser.PhoneNumber)
            {
                beforeChanges.Add($"手机号码: {originalUser.PhoneNumber ?? "空"}");
                afterChanges.Add($"手机号码: {updatedUser.PhoneNumber ?? "空"}");
            }

            if (originalUser.Status != updatedUser.Status)
            {
                beforeChanges.Add($"状态: {GetStatusDisplayName(originalUser.Status)}");
                afterChanges.Add($"状态: {GetStatusDisplayName(updatedUser.Status)}");
            }

            // 如果没有实际变化，则不记录日志
            if (!beforeChanges.Any())
                return;

            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = originalUser.Id,
                TargetName = originalUser.UserName,
                BeforeContent = $"{FormatUserName(originalUser.UserName)}\n" + string.Join("\n", beforeChanges),
                AfterContent = $"{FormatUserName(originalUser.UserName)}\n" + string.Join("\n", afterChanges)
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogUserStatusChangeAsync(ApplicationUser user, UserStatus oldStatus, UserStatus newStatus, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = user.Id,
                TargetName = user.UserName,
                BeforeContent = $"{FormatUserName(user.UserName)}\n状态: {GetStatusDisplayName(oldStatus)}",
                AfterContent = $"{FormatUserName(user.UserName)}\n状态: {GetStatusDisplayName(newStatus)}"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogPasswordChangeAsync(ApplicationUser user, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = user.Id,
                TargetName = user.UserName,
                BeforeContent = $"{FormatUserName(user.UserName)}\n密码: [已隐藏]",
                AfterContent = $"{FormatUserName(user.UserName)}\n密码: [已更新]"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogUserDeletionAsync(ApplicationUser deletedUser, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = deletedUser.Id,
                TargetName = deletedUser.UserName,
                BeforeContent = $"{FormatUserName(deletedUser.UserName)}\n状态: {GetStatusDisplayName(deletedUser.Status)}",
                AfterContent = $"{FormatUserName(deletedUser.UserName)}\n状态: {GetStatusDisplayName(UserStatus.Inactive)}"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogUserRestorationAsync(ApplicationUser user, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = user.Id,
                TargetName = user.UserName,
                BeforeContent = $"{FormatUserName(user.UserName)}\n状态: 未激活",
                AfterContent = $"{FormatUserName(user.UserName)}\n状态: 活跃"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private async Task LogUserPermanentDeletionAsync(ApplicationUser user, ApplicationUser operatorUser)
        {
            var log = new ChangeLog
            {
                OperationType = OperationType.更新,
                OperationStatus = OperationStatus.成功,
                OperatedAt = DateTime.UtcNow,
                OperationTarget = OperationTarget.用户,
                OperatedByUserId = operatorUser.Id,
                OperatedByUserName = operatorUser.UserName,
                OperatedByRealName = operatorUser.RealName,
                TargetId = user.Id,
                TargetName = user.UserName,
                BeforeContent = $"{FormatUserName(user.UserName)}\n真实姓名: {user.RealName}\n邮箱: {user.Email}\n删除状态: 已删除"
            };
            _context.ChangeLogs.Add(log);
            await _context.SaveChangesAsync();
        }

        private string GetGenderDisplayName(Gender gender)
        {
            return gender switch
            {
                Gender.Male => "男",
                Gender.Female => "女",
                _ => "未知"
            };
        }

        private string GetStatusDisplayName(UserStatus status)
        {
            return status switch
            {
                UserStatus.Active => "活跃",
                UserStatus.Inactive => "未激活",
                UserStatus.Locked => "锁定",
                _ => "未知状态"
            };
        }

        private string GetRoleDisplayName(UserRole role)
        {
            return role switch
            {
                UserRole.teamMember => "团队成员",
                UserRole.systemAdmin => "系统管理员",
                _ => "未知角色"
            };
        }

        #endregion

        #region User Operations With Log

        public async Task<(IdentityResult Result, ApplicationUser User)> CreateUserWithLogAsync(
            string userName, string realName, string email, Gender gender,
            UserRole role, string phoneNumber, string password, ApplicationUser operatorUser)
        {
            if (!await _userManager.IsInRoleAsync(operatorUser, "systemAdmin"))
                return (IdentityResult.Failed(new IdentityError { Description = "只有系统管理员可以创建用户" }), new ApplicationUser());

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var (result, user) = await CreateUserAsync(userName, realName, email, gender, role, phoneNumber, password);
                if (!result.Succeeded)
                    return (result, user);

                await LogUserCreationAsync(user, operatorUser);

                await transaction.CommitAsync();
                return (result, user);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> UpdateUserWithLogAsync(
            int id, string realName, string email, Gender gender, string phoneNumber,
            ApplicationUser operatorUser)
        {
            if (!await CanEditUserAsync(id, operatorUser.Id))
                return IdentityResult.Failed(new IdentityError { Description = "你没有编辑该用户的权限" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var originalUser = await _context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == id);

                if (originalUser == null || originalUser.IsDeleted)
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });

                var result = await UpdateUserAsync(id, realName, email, gender, phoneNumber);
                if (!result.Succeeded)
                    return result;

                var updatedUser = await _context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == id);

                if (updatedUser == null)
                    return IdentityResult.Failed(new IdentityError { Description = "更新后用户不存在" });

                await LogUserUpdateAsync(originalUser, updatedUser, operatorUser);

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> ToggleUserStatusWithLogAsync(int id, ApplicationUser operatorUser)
        {
            if (!await CanToggleUserStatusAsync(id, operatorUser.Id))
                return IdentityResult.Failed(new IdentityError { Description = "你没有切换该用户状态的权限，或该用户是最后一个有效系统管理员" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _userManager.FindByIdAsync(id.ToString());
                if (user == null || user.IsDeleted)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });
                }

                if (user.Id == operatorUser.Id)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "不能切换自己的账户状态" });
                }

                var originalStatus = user.Status;
                user.Status = user.Status == UserStatus.Active ? UserStatus.Inactive : UserStatus.Active;
                user.UpdatedAt = AppTime.Now;

                var result = await _userManager.UpdateAsync(user);

                if (result.Succeeded)
                {
                    await LogUserStatusChangeAsync(user, originalStatus, user.Status, operatorUser);
                }

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> DeleteUserWithLogAsync(int id, ApplicationUser operatorUser)
        {
            if (!await CanDeleteUserAsync(id, operatorUser.Id))
                return IdentityResult.Failed(new IdentityError { Description = "你没有删除该用户的权限，或该用户是最后一个有效系统管理员" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == id);

                if (user == null || user.IsDeleted)
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });

                var result = await DeleteUserAsync(id, operatorUser.Id);
                if (!result.Succeeded)
                    return result;

                await LogUserDeletionAsync(user, operatorUser);

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> ChangePasswordWithLogAsync(
            int userId,
            string currentPassword,
            string newPassword,
            ApplicationUser operatorUser)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _userManager.FindByIdAsync(userId.ToString());
                if (user == null)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在" });
                }

                var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
                if (!result.Succeeded)
                {
                    return result;
                }

                await LogPasswordChangeAsync(user, operatorUser);

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> RestoreUserWithLogAsync(int id, ApplicationUser operatorUser)
        {
            if (!await _userManager.IsInRoleAsync(operatorUser, "systemAdmin"))
                return IdentityResult.Failed(new IdentityError { Description = "只有系统管理员可以恢复用户" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _userManager.FindByIdAsync(id.ToString());
                if (user == null || !user.IsDeleted)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或未被删除" });
                }

                var result = await RestoreUserAsync(id);
                if (!result.Succeeded)
                    return result;

                await LogUserRestorationAsync(user, operatorUser);

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IdentityResult> PermanentlyDeleteUserWithLogAsync(int id, ApplicationUser operatorUser)
        {
            if (!await _userManager.IsInRoleAsync(operatorUser, "systemAdmin") || id == operatorUser.Id)
                return IdentityResult.Failed(new IdentityError { Description = "你没有永久删除该用户的权限" });

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _userManager.FindByIdAsync(id.ToString());
                if (user == null || !user.IsDeleted)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或未被删除" });
                }

                var result = await PermanentlyDeleteUserAsync(id, operatorUser.Id);
                if (!result.Succeeded)
                    return result;

                await LogUserPermanentDeletionAsync(user, operatorUser);

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        #endregion

        #region User Query Methods

        public async Task<ApplicationUser?> GetCurrentUserAsync(ClaimsPrincipal principal)
        {
            return await _userManager.GetUserAsync(principal);
        }

        public async Task<bool> IsUserInRoleAsync(ApplicationUser user, string role)
        {
            return await _userManager.IsInRoleAsync(user, role);
        }

        public async Task<(IList<ApplicationUser> Users, int TotalCount)> GetSortedUsersAsync(
            string searchString,
            string genderFilter,
            string statusFilter,
            bool showDeleted,
            int currentPage,
            int pageSize)
        {
            var usersQuery = _context.Users.AsQueryable();

            if (!showDeleted)
            {
                usersQuery = usersQuery.Where(u => !u.IsDeleted);
            }

            if (!string.IsNullOrEmpty(searchString))
            {
                usersQuery = usersQuery.Where(u =>
                    (u.UserName != null && u.UserName.Contains(searchString)) ||
                    (u.RealName != null && u.RealName.Contains(searchString)) ||
                    (u.Email != null && u.Email.Contains(searchString)));
            }

            if (!string.IsNullOrEmpty(genderFilter) &&
                Enum.TryParse<Gender>(genderFilter, out var gender))
            {
                usersQuery = usersQuery.Where(u => u.Gender == gender);
            }

            if (!string.IsNullOrEmpty(statusFilter) &&
                Enum.TryParse<UserStatus>(statusFilter, out var status))
            {
                usersQuery = usersQuery.Where(u => u.Status == status);
            }

            var totalCount = await usersQuery.CountAsync();
            var users = await usersQuery
                .Skip((currentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (users, totalCount);
        }

        public async Task<ApplicationUser?> GetUserDetailsAsync(int id)
        {
            return await _context.Users
                .Include(u => u.CreatedProjects)
                .FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        }

        public async Task<List<ProjectListDto>> GetUserProjectsAsync(int userId)
        {
            return await _context.Project
                .Where(p => p.CreatedByUserId == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new ProjectListDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description ?? string.Empty,
                    CreatedAt = p.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<List<string>> GetUserRolesAsync(ApplicationUser user)
        {
            return (await _userManager.GetRolesAsync(user)).ToList();
        }

        public async Task<IList<ApplicationUser>> GetDeletedUsersAsync()
        {
            return await _context.Users
                .Where(u => u.IsDeleted)
                .OrderByDescending(u => u.DeletedAt)
                .ToListAsync();
        }

        #endregion

        #region Validation Methods

        public async Task<bool> CheckUserNameExistsAsync(string userName)
        {
            return await _userManager.FindByNameAsync(userName) != null;
        }

        public async Task<bool> CheckEmailExistsAsync(string email)
        {
            return await _userManager.FindByEmailAsync(email) != null;
        }

        public async Task<bool> CanEditUserAsync(int targetUserId, int currentUserId)
        {
            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());

            if (targetUser == null || currentUser == null) return false;

            // 普通用户只能编辑自己；系统管理员可以编辑所有用户。
            if (targetUserId == currentUserId) return true;
            return await _userManager.IsInRoleAsync(currentUser, "systemAdmin");
        }

        public async Task<bool> CanToggleUserStatusAsync(int targetUserId, int currentUserId)
        {
            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());

            if (targetUser == null || currentUser == null) return false;
            if (targetUserId == currentUserId) return false;
            if (!await _userManager.IsInRoleAsync(currentUser, "systemAdmin")) return false;
            return !await IsLastActiveSystemAdminAsync(targetUser);
        }

        public async Task<bool> CanDeleteUserAsync(int targetUserId, int currentUserId)
        {
            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());

            if (targetUser == null || currentUser == null) return false;
            if (targetUserId == currentUserId) return false;
            if (!await _userManager.IsInRoleAsync(currentUser, "systemAdmin")) return false;
            return !await IsLastActiveSystemAdminAsync(targetUser);
        }

        #endregion

        #region Core User Operations

        public async Task<(IdentityResult Result, ApplicationUser User)> CreateUserAsync(
            string userName,
            string realName,
            string email,
            Gender gender,
            UserRole role,
            string phoneNumber,
            string password)
        {
            var user = new ApplicationUser
            {
                UserName = userName,
                Email = email,
                RealName = realName,
                Gender = gender,
                Role = role,
                Status = UserStatus.Active,
                PhoneNumber = phoneNumber,
                CreatedAt = AppTime.Now
            };

            var result = await _userManager.CreateAsync(user, password);

            if (result.Succeeded)
            {
                var roleName = role == UserRole.systemAdmin ? "systemAdmin" : "teamMember";
                await _userManager.AddToRoleAsync(user, roleName);
            }

            return (result, user);
        }

        public async Task<IdentityResult> UpdateUserAsync(
            int id,
            string realName,
            string email,
            Gender gender,
            string phoneNumber)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null || user.IsDeleted)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });
            }

            var existingEmailUser = await _userManager.FindByEmailAsync(email);
            if (existingEmailUser != null && existingEmailUser.Id != user.Id)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Description = "保存失败：该邮箱已被其他用户使用，请更换邮箱后重试"
                });
            }

            user.RealName = realName;
            user.Email = email;
            user.Gender = gender;
            user.PhoneNumber = phoneNumber;
            user.UpdatedAt = AppTime.Now;

            return await _userManager.UpdateAsync(user);
        }

        public async Task<IdentityResult> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在" });
            }

            return await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        }

        public async Task<IdentityResult> DeleteUserAsync(int id, int currentUserId)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());
            if (user == null || user.IsDeleted)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });
            }

            if (user.Id == currentUserId)
            {
                return IdentityResult.Failed(new IdentityError { Description = "不能删除自己的账户" });
            }

            if (currentUser == null || !await _userManager.IsInRoleAsync(currentUser, "systemAdmin"))
                return IdentityResult.Failed(new IdentityError { Description = "只有系统管理员可以删除用户" });
            if (await IsLastActiveSystemAdminAsync(user))
                return IdentityResult.Failed(new IdentityError { Description = "不能删除最后一个有效的系统管理员" });

            user.IsDeleted = true;
            user.DeletedAt = AppTime.Now;
            user.Status = UserStatus.Inactive;

            return await _userManager.UpdateAsync(user);
        }

        public async Task<IdentityResult> RestoreUserAsync(int id)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            if (user == null || !user.IsDeleted)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在或未被删除" });
            }

            user.IsDeleted = false;
            user.DeletedAt = null;
            user.Status = UserStatus.Active;
            user.UpdatedAt = AppTime.Now;

            return await _userManager.UpdateAsync(user);
        }

        public async Task<IdentityResult> PermanentlyDeleteUserAsync(int id, int currentUserId)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());
            if (user == null || !user.IsDeleted)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在或未被删除" });
            }

            if (user.Id == currentUserId)
            {
                return IdentityResult.Failed(new IdentityError { Description = "不能永久删除自己的账户" });
            }

            if (currentUser == null || !await _userManager.IsInRoleAsync(currentUser, "systemAdmin"))
                return IdentityResult.Failed(new IdentityError { Description = "只有系统管理员可以永久删除用户" });

            var result = await _userManager.DeleteAsync(user);
            return result;
        }

        public async Task<bool> HasUserChangesAsync(int userId, UserEditDto dto)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return false;

            return user.RealName != dto.RealName ||
                   user.Email != dto.Email ||
                   user.Gender != dto.Gender ||
                   user.PhoneNumber != dto.PhoneNumber;
        }

        public async Task<IdentityResult> ValidateAndUpdateUserAsync(UserEditDto dto)
        {
            var errors = new List<IdentityError>();

            var existingUser = await _userManager.FindByEmailAsync(dto.Email);
            if (existingUser != null && existingUser.Id != dto.Id)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Description = "保存失败：该邮箱已被其他用户使用，请更换邮箱后重试"
                });
            }

            if (!string.IsNullOrEmpty(dto.PhoneNumber))
            {
                var phoneRegex = new Regex(@"^1[3-9]\d{9}$");
                if (!phoneRegex.IsMatch(dto.PhoneNumber))
                {
                    errors.Add(new IdentityError { Description = "请输入有效的中国手机号码" });
                }
            }

            if (errors.Count > 0)
            {
                return IdentityResult.Failed(errors.ToArray());
            }

            return await UpdateUserAsync(
                dto.Id,
                dto.RealName,
                dto.Email,
                dto.Gender,
                dto.PhoneNumber ?? string.Empty);
        }
        #endregion

        #region Additional User Operations

        public async Task<IdentityResult> UpdateUserPhoneNumberAsync(
            int id,
            string phoneNumber,
            ApplicationUser operatorUser)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _userManager.FindByIdAsync(id.ToString());
                if (user == null || user.IsDeleted)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });
                }

                // 检查是否有实际变更
                if (user.PhoneNumber == phoneNumber)
                {
                    return IdentityResult.Failed(new IdentityError { Description = "没有检测到变更" });
                }

                var originalPhoneNumber = user.PhoneNumber;
                user.PhoneNumber = phoneNumber;
                user.UpdatedAt = AppTime.Now;

                var result = await _userManager.UpdateAsync(user);

                if (result.Succeeded)
                {
                    // 记录日志
                    var log = new ChangeLog
                    {
                        OperationType = OperationType.更新,
                        OperationStatus = OperationStatus.成功,
                        OperatedAt = DateTime.UtcNow,
                        OperationTarget = OperationTarget.用户,
                        OperatedByUserId = operatorUser.Id,
                        OperatedByUserName = operatorUser.UserName,
                        OperatedByRealName = operatorUser.RealName,
                        TargetId = user.Id,
                        TargetName = user.UserName,
                        BeforeContent = $"{FormatUserName(user.UserName)}\n手机号码: {originalPhoneNumber ?? "空"}",
                        AfterContent = $"{FormatUserName(user.UserName)}\n手机号码: {phoneNumber ?? "空"}"
                    };
                    _context.ChangeLogs.Add(log);
                    await _context.SaveChangesAsync();
                }

                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // 保持原有的 ToggleUserStatusAsync 方法（非事务版本），供其他地方调用
        public async Task<IdentityResult> ToggleUserStatusAsync(int id, int currentUserId)
        {
            var user = await _userManager.FindByIdAsync(id.ToString());
            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());
            if (user == null || user.IsDeleted)
            {
                return IdentityResult.Failed(new IdentityError { Description = "用户不存在或已被删除" });
            }

            if (user.Id == currentUserId)
            {
                return IdentityResult.Failed(new IdentityError { Description = "不能切换自己的账户状态" });
            }

            if (currentUser == null || !await _userManager.IsInRoleAsync(currentUser, "systemAdmin"))
                return IdentityResult.Failed(new IdentityError { Description = "只有系统管理员可以切换用户状态" });
            if (await IsLastActiveSystemAdminAsync(user))
                return IdentityResult.Failed(new IdentityError { Description = "不能停用最后一个有效的系统管理员" });

            var originalStatus = user.Status;
            user.Status = user.Status == UserStatus.Active ? UserStatus.Inactive : UserStatus.Active;
            user.UpdatedAt = AppTime.Now;

            var result = await _userManager.UpdateAsync(user);

            if (result.Succeeded)
            {
                var operatorUser = await _userManager.FindByIdAsync(currentUserId.ToString());
                if (operatorUser != null)
                    await LogUserStatusChangeAsync(user, originalStatus, user.Status, operatorUser);
            }

            return result;
        }

        private async Task<bool> IsLastActiveSystemAdminAsync(ApplicationUser user)
        {
            if (user.IsDeleted || user.Status != UserStatus.Active
                || !await _userManager.IsInRoleAsync(user, "systemAdmin"))
                return false;

            var administrators = await _userManager.GetUsersInRoleAsync("systemAdmin");
            return administrators.Count(item => !item.IsDeleted && item.Status == UserStatus.Active) <= 1;
        }

        #endregion
    }
}
