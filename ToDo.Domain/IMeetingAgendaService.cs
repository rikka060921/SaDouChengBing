using System.Collections.Generic;
using System.Threading.Tasks;
using ToDo.Entities;

namespace ToDo.Domain
{
    public interface IMeetingAgendaService
    {
        Task<List<MeetingAgenda>> GetAgendasByProjectAsync(int projectId);
        Task<MeetingAgenda> AddAgendaAsync(MeetingAgenda agenda);
        Task<List<MeetingAgenda>> AddAgendasAsync(List<MeetingAgenda> agendas);
        Task<MeetingAgenda> UpdateAgendaAsync(MeetingAgenda agenda);
        Task<bool> DeleteAgendaAsync(int id, int projectId);
        Task<bool> CanEditAgendaAsync(int projectId, ApplicationUser user);
    }
}
