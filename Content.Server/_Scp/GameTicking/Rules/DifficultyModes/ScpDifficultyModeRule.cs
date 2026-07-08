using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Content.Server._Scp.Utility.Helpers;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared._Scp.Anomaly;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Item;
using Content.Shared.Roles;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._Scp.GameTicking.Rules.DifficultyModes;

public sealed partial class ScpDifficultyModeRule : GameRuleSystem<ScpDifficultyModeRuleComponent>
{
    [Dependency] private StationJobsSystem _stationJobs = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private ScpHelpersSystem _helpers = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ScpComponent, PlayerSpawnCompleteEvent>(OnPlayerSpawn);
        SubscribeLocalEvent<StationJobAssignedEvent>(OnStationJobAssigned);
    }

    /// <summary>
    /// Срабатывает при спавне игрока на SCP объекте и закрывает слоты других объектов, если их лимит превышен.
    /// Лимиты задаются в текущем режиме сложности как количество определенных классов содержания в раунде.
    /// Если игрок с данным классом содержания заходит в раунд, то из всех слотов SCP объектов с данным классом также вычитается 1 слот
    /// </summary>
    private void OnPlayerSpawn(Entity<ScpComponent> ent, ref PlayerSpawnCompleteEvent args)
    {
        if (args.JobId == null)
            return;

        if (!TryGetActiveDifficultyRule(out var rule))
            return;

        // Получаем все работки SCP, которые соответствуют классу содержания зашедшего SCP объекта
        var matchingScp = GetMatchingScpJobs(ent.Comp.Class, rule);

        // Забираем у каждого SCP с классом схожим с только что зашедшим слот
        foreach (var scp in matchingScp)
        {
            if (scp.ID == args.JobId)
                continue;

            if (rule.ScpSlots.TryGetValue(ent.Comp.Class, out var slots) && slots == ScpDifficultyModeRuleComponent.UnlimitedSlotsFlag)
                continue;

            if (!_stationJobs.TryGetJobSlot(args.Station, scp, out var currentSlots))
                continue;

            if (currentSlots == null || currentSlots <= 0)
                continue;

            _stationJobs.TrySetJobSlot(args.Station, scp, currentSlots.Value - 1);
        }
    }

    private void OnStationJobAssigned(ref StationJobAssignedEvent args)
    {
        if (!TryGetActiveDifficultyRule(out var rule))
            return;

        if (!TryGetScpClassification(args.Job, rule, out var classification))
            return;

        if (rule.ScpSlots.TryGetValue(classification, out var slots) &&
            slots == ScpDifficultyModeRuleComponent.UnlimitedSlotsFlag)
        {
            return;
        }

        DecrementMatchingScpJobs(classification, args.Job, rule, args.StationJobs);
        DecrementMatchingScpJobs(classification, args.Job, rule, args.CurrentJobs);
    }

    protected override void Started(EntityUid uid,
        ScpDifficultyModeRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var targetStation))
            return;

        // Проходимся по всем работкам и сцп-предметами, устанавливая текущие правила игры
        // Слоты на неподходящие работы закрываются, а предметы удаляются

        DealWithPlayableScp(component, targetStation.Value);
        DealWithItems(component, targetStation.Value);
    }

    private void DealWithPlayableScp(ScpDifficultyModeRuleComponent component, EntityUid targetStation)
    {
        foreach (var (classification, slots) in component.ScpSlots)
        {
            if (slots == ScpDifficultyModeRuleComponent.UnlimitedSlotsFlag)
                continue;

            // Получаем все работки SCP, которые соответствуют текущему классу содержания
            var matchingScp = ProtoMan.EnumeratePrototypes<JobPrototype>()
                .Where(proto => IsMatchingScpJob(classification, proto, component.PlayableWhitelist, component.PlayableBlacklist));

            // Подсчитываем, сколько будет доступно слотов
            var targetSlots = slots.Next(_random);

            // Устанавливаем для каждого подходящего SCP количество слотов
            foreach (var job in matchingScp)
            {
                _stationJobs.TrySetRoundStartJobSlot(targetStation, job, targetSlots);
                _stationJobs.TrySetJobSlot(targetStation, job, targetSlots);
            }
        }
    }

    /// <summary>
    /// Проходится по SCP предметам и удаляем неразрешенные
    /// </summary>
    private void DealWithItems(ScpDifficultyModeRuleComponent component, EntityUid targetStation)
    {
        // Находим все неподходящие для данного режима предметы
        var inappropriateItems = _helpers.GetAll<ScpComponent, ItemComponent>()
            .Where(item =>
                !IsMatchingItem((item, item.Comp1), component.ItemWhitelist, component.ItemBlacklist, targetStation))
            .ToList();

        // И удаляем их
        foreach (var item in inappropriateItems)
        {
            QueueDel(item);
        }
    }

    /// <summary>
    /// Проверяет, является ли данная работа SCP и подходит ли под требования режима
    /// </summary>
    /// <returns>Да/Нет</returns>
    private bool IsMatchingScpJob(Classification classification, JobPrototype job, ComponentRegistry? whitelist, ComponentRegistry? blacklist)
    {
        if (!TryGetScpClassification(job, whitelist, blacklist, out var jobClassification))
            return false;

        return jobClassification == classification;
    }

    /// <summary>
    /// Проверяет, подходит ли данный предмет под требования режима
    /// </summary>
    /// <returns>Да/Нет</returns>
    private bool IsMatchingItem(Entity<ScpComponent> item, EntityWhitelist? whitelist, EntityWhitelist? blacklist, EntityUid targetStation)
    {
        if (!_whitelist.CheckBoth(item, blacklist, whitelist))
            return false;

        var station = _station.GetOwningStation(item);

        if (station != targetStation)
            return false;

        return true;
    }

    private bool TryGetActiveDifficultyRule([NotNullWhen(true)] out ScpDifficultyModeRuleComponent? rule)
    {
        var isGameModeStarted = _ticker.GetActiveGameRules()
            .Where(HasComp<ScpDifficultyModeRuleComponent>)
            .Select(Comp<ScpDifficultyModeRuleComponent>)
            .TryFirstOrDefault(out rule);

        return isGameModeStarted && rule != null;
    }

    private IEnumerable<JobPrototype> GetMatchingScpJobs(Classification classification, ScpDifficultyModeRuleComponent rule)
    {
        return ProtoMan.EnumeratePrototypes<JobPrototype>()
            .Where(proto => IsMatchingScpJob(classification, proto, rule.PlayableWhitelist, rule.PlayableBlacklist));
    }

    private bool TryGetScpClassification(
        ProtoId<JobPrototype> jobId,
        ScpDifficultyModeRuleComponent rule,
        out Classification classification)
    {
        classification = default;

        if (!ProtoMan.TryIndex(jobId, out var job))
            return false;

        return TryGetScpClassification(job, rule.PlayableWhitelist, rule.PlayableBlacklist, out classification);
    }

    private bool TryGetScpClassification(
        JobPrototype job,
        ComponentRegistry? whitelist,
        ComponentRegistry? blacklist,
        out Classification classification)
    {
        classification = default;

        if (job.JobEntity == null)
            return false;

        if (!ProtoMan.TryIndex(job.JobEntity, out var entity))
            return false;

        // Реализация вайтлиста. Так как в вайтлисте будет перечисление компонентов, которые будут представлять сцп.
        // То нам нужно, чтобы хотя бы один совпал.
        if (whitelist != null && !entity.Components.Any(whitelist.Contains))
            return false;

        // Обратная ситуация с блеклистом. Нужно, чтобы не совпал ни один.
        // Следовательно, делаем возврат, если найден хоть один.
        if (blacklist != null && entity.Components.Any(blacklist.Contains))
            return false;

        if (!entity.Components.TryGetComponent("Scp", out var component))
            return false;

        if (component is not ScpComponent scpComponent)
            return false;

        classification = scpComponent.Class;
        return true;
    }

    private void DecrementMatchingScpJobs(
        Classification classification,
        ProtoId<JobPrototype> assignedJob,
        ScpDifficultyModeRuleComponent rule,
        Dictionary<ProtoId<JobPrototype>, int?> jobs)
    {
        foreach (var scp in GetMatchingScpJobs(classification, rule))
        {
            if (scp.ID == assignedJob)
                continue;

            DecrementJobSlot(jobs, scp.ID);
        }
    }

    private static void DecrementJobSlot(Dictionary<ProtoId<JobPrototype>, int?> jobs, ProtoId<JobPrototype> job)
    {
        if (!jobs.TryGetValue(job, out var currentSlots))
            return;

        if (currentSlots == null || currentSlots <= 0)
            return;

        jobs[job] = currentSlots.Value - 1;
    }
}
