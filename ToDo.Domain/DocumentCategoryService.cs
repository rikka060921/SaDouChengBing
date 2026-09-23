using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class DocumentCategoryService
{
    private readonly ApplicationDbContext _context;

    public DocumentCategoryService(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<List<DocumentCategory>> GetCategoriesAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return _context.DocumentCategories.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<DocumentCategory?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.DocumentCategories.FindAsync(id, cancellationToken);
    }

    public async Task<DocumentCategory> CreateAsync(
        int projectId,
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        // 检查是否与预设分类名称重复
        var presetNames = Enum.GetValues<ProjectDocumentCategory>()
            .Select(c => c.GetDisplayName())
            .ToList();
        
        if (presetNames.Contains(name))
        {
            throw new InvalidOperationException($"分类名「{name}」与预设分类重复，请使用其他名称");
        }

        var existing = await _context.DocumentCategories
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Name == name, cancellationToken);

        if (existing != null) return existing;

        var category = new DocumentCategory
        {
            ProjectId = projectId,
            Name = name,
            Description = description,
            IsDefault = false,
            SortOrder = 0
        };

        _context.DocumentCategories.Add(category);
        await _context.SaveChangesAsync(cancellationToken);
        return category;
    }

    public async Task<DocumentCategory> GetOrCreateAsync(
        int projectId,
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        return await CreateAsync(projectId, name, description, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var category = await _context.DocumentCategories.FindAsync(id, cancellationToken);
        if (category != null)
        {
            _context.DocumentCategories.Remove(category);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task InitializeDefaultCategoriesAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var defaults = new[]
        {
            ProjectDocumentCategory.RequirementsAndGoals.GetDisplayName(),
            ProjectDocumentCategory.MeetingMinutes.GetDisplayName(),
            ProjectDocumentCategory.TasksAndPlans.GetDisplayName(),
            ProjectDocumentCategory.DailyReportsAndReviews.GetDisplayName(),
            ProjectDocumentCategory.TechnicalMaterials.GetDisplayName(),
            ProjectDocumentCategory.PoliciesAndStandards.GetDisplayName(),
            ProjectDocumentCategory.Other.GetDisplayName()
        };

        foreach (var name in defaults)
        {
            var exists = await _context.DocumentCategories.AnyAsync(
                c => c.ProjectId == projectId && c.Name == name, cancellationToken);
            if (!exists)
            {
                _context.DocumentCategories.Add(new DocumentCategory
                {
                    ProjectId = projectId,
                    Name = name,
                    IsDefault = true,
                    SortOrder = Array.IndexOf(defaults, name)
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
