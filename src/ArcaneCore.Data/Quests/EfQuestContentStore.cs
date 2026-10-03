using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Quests;

/// <summary>Reads the quest content tables of world schema v2 (startup only).</summary>
public sealed class EfQuestContentStore(WorldDbContext db) : IQuestContentStore
{
    public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<QuestTemplate> templates = await db.Set<QuestTemplate>().AsNoTracking()
            .OrderBy(q => q.Entry).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureQuestRelation> starters = await db.Set<CreatureQuestStarterRow>().AsNoTracking()
            .OrderBy(r => r.Id).ThenBy(r => r.Quest)
            .Select(r => new CreatureQuestRelation { Id = r.Id, Quest = r.Quest })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureQuestRelation> enders = await db.Set<CreatureQuestEnderRow>().AsNoTracking()
            .OrderBy(r => r.Id).ThenBy(r => r.Quest)
            .Select(r => new CreatureQuestRelation { Id = r.Id, Quest = r.Quest })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The game object relations live in the game object world module (gameobject_questrelation /
        // gameobject_involvedrelation, vmangos GetGOQuestRelationsMapBounds / GetGOQuestInvolvedRelationsMapBounds).
        List<CreatureQuestRelation> goStarters = await db.Set<GameObjectQuestStarterRow>().AsNoTracking()
            .OrderBy(r => r.Id).ThenBy(r => r.Quest)
            .Select(r => new CreatureQuestRelation { Id = r.Id, Quest = r.Quest })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureQuestRelation> goEnders = await db.Set<GameObjectQuestEnderRow>().AsNoTracking()
            .OrderBy(r => r.Id).ThenBy(r => r.Quest)
            .Select(r => new CreatureQuestRelation { Id = r.Id, Quest = r.Quest })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new QuestContent(templates, starters, enders) { GameObjectStarters = goStarters, GameObjectEnders = goEnders };
    }
}
