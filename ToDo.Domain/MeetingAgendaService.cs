using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain
{
    public class MeetingAgendaService : IMeetingAgendaService
    {
        private readonly ApplicationDbContext _context;

        public MeetingAgendaService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<MeetingAgenda>> GetAgendasByProjectAsync(int projectId)
        {
            return await _context.MeetingAgendas
                .Where(a => a.ProjectId == projectId && !a.IsDeleted)
                .OrderBy(a => a.SortOrder)
                .ToListAsync();
        }

        public async Task<MeetingAgenda> AddAgendaAsync(MeetingAgenda agenda)
        {
            var maxSort = await _context.MeetingAgendas
                .Where(a => a.ProjectId == agenda.ProjectId && !a.IsDeleted)
                .MaxAsync(a => (int?)a.SortOrder) ?? 0;

            agenda.SortOrder = maxSort + 1;
            _context.MeetingAgendas.Add(agenda);
            await _context.SaveChangesAsync();
            return agenda;
        }

        public async Task<List<MeetingAgenda>> AddAgendasAsync(List<MeetingAgenda> agendas)
        {
            if (agendas == null || !agendas.Any())
                return new List<MeetingAgenda>();

            var projectId = agendas.First().ProjectId;
            var maxSort = await _context.MeetingAgendas
                .Where(a => a.ProjectId == projectId && !a.IsDeleted)
                .MaxAsync(a => (int?)a.SortOrder) ?? 0;

            for (int i = 0; i < agendas.Count; i++)
            {
                agendas[i].SortOrder = maxSort + i + 1;
                _context.MeetingAgendas.Add(agendas[i]);
            }

            await _context.SaveChangesAsync();
            return agendas;
        }

        public async Task<MeetingAgenda> UpdateAgendaAsync(MeetingAgenda agenda)
        {
            agenda.LastModifiedAt = AppTime.Now;
            _context.MeetingAgendas.Update(agenda);
            await _context.SaveChangesAsync();
            return agenda;
        }

        public async Task<bool> DeleteAgendaAsync(int id, int projectId)
        {
            var agenda = await _context.MeetingAgendas
                .FirstOrDefaultAsync(a => a.Id == id && a.ProjectId == projectId && !a.IsDeleted && a.Status == AgendaStatus.Active);

            if (agenda == null)
                return false;

            agenda.IsDeleted = true;
            agenda.LastModifiedAt = AppTime.Now;
            _context.MeetingAgendas.Update(agenda);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> CanEditAgendaAsync(int projectId, ApplicationUser user)
        {
            var project = await _context.Project
                .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

            if (project == null)
                return false;

            // 议题操作权限：系统管理员或本项目成员均可操作
            if (user.Role == UserRole.systemAdmin)
                return true;

            var isProjectMember = await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == projectId && pu.UserId == user.Id);

            return isProjectMember;
        }
    }
}
