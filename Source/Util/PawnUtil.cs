using System;
using System.Collections.Generic;
using System.Linq;
using RimTalk.Data;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Cache = RimTalk.Data.Cache;

namespace RimTalk.Util;

public static class PawnUtil
{
    public static bool IsTalkEligible(this Pawn pawn)
    {
        if (pawn.IsPlayer()) return true;
        if (pawn.HasVocalLink()) return true;
        if (pawn.DestroyedOrNull() || !pawn.Spawned || pawn.Dead) return false;
        if (!pawn.RaceProps.Humanlike && !pawn.RaceProps.ToolUser) return false;
        if (pawn.RaceProps.intelligence < Intelligence.ToolUser) return false;
        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)&& !pawn.RaceProps.ToolUser) return false;
        if (pawn.skills?.GetSkill(SkillDefOf.Social) == null) return false;

        RimTalkSettings settings = Settings.Get();
        return pawn.IsFreeColonist ||
               (settings.AllowSlavesToTalk && pawn.IsSlave) ||
               (settings.AllowPrisonersToTalk && pawn.IsPrisoner) ||
               (settings.AllowOtherFactionsToTalk && pawn.IsVisitor()) ||
               (settings.AllowEnemiesToTalk && pawn.IsEnemy()) ||
               (settings.AllowBabiesToTalk && pawn.IsBaby()) ||
               pawn.RaceProps.ToolUser;
    }

    public static HashSet<Hediff> GetHediffs(this Pawn pawn)
    {
        return pawn?.health.hediffSet.hediffs.Where(hediff => hediff.Visible).ToHashSet();
    }

    public static bool IsInDanger(this Pawn pawn, bool includeMentalState = false)
    {
        if (pawn == null || pawn.IsPlayer()) return false;
        if (pawn.Dead) return true;
        if (pawn.Downed) return true;
        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) return true;
        if (pawn.InMentalState && includeMentalState) return true;
        if (pawn.IsBurning()) return true;
        if (pawn.health.hediffSet.PainTotal >= pawn.GetStatValue(StatDefOf.PainShockThreshold)) return true;
        if (pawn.health.hediffSet.BleedRateTotal > 0.3f) return true;
        if (pawn.IsInCombat()) return true;
        if (pawn.CurJobDef == JobDefOf.Flee) return true;

        // Check severe Hediffs
        foreach (var h in pawn.health.hediffSet.hediffs)
        {
            if (h.Visible && (h.CurStage?.lifeThreatening == true ||
                              h.def.lethalSeverity > 0 && h.Severity > h.def.lethalSeverity * 0.8f))
                return true;
        }

        return false;
    }

    public static bool IsInCombat(this Pawn pawn)
    {
        if (pawn == null) return false;

        // 1. MindState target
        if (pawn.mindState.enemyTarget != null) return true;

        // 2. Stance busy with attack verb
        if (pawn.stances?.curStance is Stance_Busy busy && busy.verb != null)
            return true;

        Pawn hostilePawn = pawn.GetHostilePawnNearBy();
        return hostilePawn != null && pawn.Position.DistanceTo(hostilePawn.Position) <= 20f;
    }

    public static string GetRole(this Pawn pawn, bool includeFaction = false)
    {
        if (pawn == null) return null;
        if (pawn.IsPlayer()) return "来自世界外的声音";
        if (pawn.IsPrisoner) return "囚犯";
        if (pawn.IsSlave) return "奴隶";
        if (pawn.IsEnemy())
            if (pawn.GetMapRole() == MapRole.Invading)
                return includeFaction && pawn.Faction != null ? $"敌方部队({pawn.Faction.Name})" : "敌人";
            else
                return "敌方防御部队";
        if (pawn.IsTrader())
            return includeFaction && pawn.Faction != null ? $"商队({pawn.Faction.Name},与用户殖民地关系:{pawn.Faction.PlayerGoodwill})" : "商人";
        if (pawn.IsVisitor())
            return includeFaction && pawn.Faction != null ? $"来访人群({pawn.Faction.Name},与用户殖民地关系:{pawn.Faction.PlayerGoodwill})" : "访客";
        if (pawn.IsQuestLodger()) return "住客";
        if (pawn.IsFreeColonist) return pawn.GetMapRole() == MapRole.Invading ? "攻击者" : "殖民者";
        return null;
    }

    public static bool IsTrader(this Pawn pawn)
    {
        if (pawn?.Faction == null || pawn.Faction == Faction.OfPlayer || pawn.HostileTo(Faction.OfPlayer))return false;
        var lord = pawn?.GetLord();
        var job = lord?.LordJob;
        return job is LordJob_TradeWithColony;
    }

    public static bool IsVisitor(this Pawn pawn)
    {
        return pawn?.Faction != null && !pawn.IsPrisoner && pawn.Faction != Faction.OfPlayer && !pawn.HostileTo(Faction.OfPlayer);
    }

    public static bool IsEnemy(this Pawn pawn)
    {
        return pawn != null && pawn.HostileTo(Faction.OfPlayer);
    }

    public static bool IsBaby(this Pawn pawn)
    {
        return pawn.ageTracker?.CurLifeStage?.developmentalStage < DevelopmentalStage.Child;
    }

    public static (string, bool) GetPawnStatusFull(this Pawn pawn, List<Pawn> nearbyPawns)
    {
        if (pawn == null)
            return (null, false);

        // special-case for "player pawn"
        if (pawn.IsPlayer())
            return ("来自世界之外的声音", false);

        bool isInDanger = false;
        var lines = new List<string>();

        // collect all "relevant pawns"
        var relevantPawns = new List<Pawn> { pawn };
        if (nearbyPawns != null)
            relevantPawns.AddRange(nearbyPawns);

        if (pawn.CurJob != null)
            AddJobTargetsToRelevantPawns(pawn.CurJob, relevantPawns);

        if (nearbyPawns != null)
        {
            foreach (var near in nearbyPawns.Where(near => near.CurJob != null))
                AddJobTargetsToRelevantPawns(near.CurJob, relevantPawns);
        }

        // first line uses name + activity AFTER name replacement
        string activity = ReplacePawnNames(pawn.GetActivity());
        string name = ReplacePawnNames(pawn.LabelShort);
        lines.Add($"{name} {activity}");

        if (pawn.IsInDanger())
            isInDanger = true;

        // Nearby critical statuses: same logic, but wrapped in ReplacePawnNames(...)
        if (nearbyPawns != null && nearbyPawns.Any())
        {
            var nearbyNotable = nearbyPawns
                .Where(p => p.Faction == pawn.Faction && p.IsInDanger(true))
                .Take(2)
                .Select(other =>
                {
                    string otherActivity = ReplacePawnNames(other.GetActivity());
                    return $"{ReplacePawnNames(other.LabelShort)} in {otherActivity.Replace("\n", "; ")}";
                })
                .ToList();

            if (nearbyNotable.Any())
            {
                lines.Add("附近状态值得关心的人: " + string.Join("; ", nearbyNotable));
                isInDanger = true;
            }

            var nearbyList = nearbyPawns
                .Select(p =>
                {
                    string s = ReplacePawnNames(p.LabelShort);
                    if (Cache.Get(p) != null)
                    {
                        string a = ReplacePawnNames(p.GetActivity());
                        s = $"{s} {a.StripTags()}";
                    }
                    return s;
                })
                .ToList();

            string nearbyStr =
                nearbyList.Count == 0 ? "无" :
                nearbyList.Count > 3 ? string.Join(", ", nearbyList.Take(3)) + ", a以及其他人" :
                string.Join(", ", nearbyList);

            lines.Add("附近的人: " + nearbyStr);
        }
        else
        {
            lines.Add("附近没有人");
        }

        if (pawn.IsVisitor())
        {
            lines.Add("正在拜访用户的殖民地");
        }

        if (pawn.IsFreeColonist && pawn.GetMapRole() == MapRole.Invading)
        {
            lines.Add("你正远离殖民地,进攻敌军据点");
        }
        else if (pawn.IsEnemy())
        {
            if (pawn.GetMapRole() == MapRole.Invading)
            {
                if (pawn.GetLord()?.LordJob is LordJob_StageThenAttack || pawn.GetLord()?.LordJob is LordJob_Siege)
                {
                    lines.Add("正准备入侵用户的殖民地");
                }
                else
                {
                    lines.Add("正在入侵用户的殖民地");
                }
            }
            else
            {
                lines.Add("为家园不被攻陷而战");
            }

            return (string.Join("\n", lines), isInDanger);
        }

        // --- 3. Enemy proximity / combat info ---
        Pawn nearestHostile = GetHostilePawnNearBy(pawn);
        if (nearestHostile != null)
        {
            float distance = pawn.Position.DistanceTo(nearestHostile.Position);

            if (distance <= 10f)
                lines.Add("威胁: 正在交火!");
            else if (distance <= 20f)
                lines.Add("威胁: 敌人逼近!");
            else
                lines.Add("警告: 这片区域附近存在敌人");
            isInDanger = true;
        }

        if (!isInDanger)
            lines.Add(Constant.Prompt);

        return (string.Join("\n", lines), isInDanger);

        string ReplacePawnNames(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var map = new Dictionary<string, string>();
            foreach (var rp in relevantPawns)
            {
                string key = rp.LabelShort;
                string value = ContextHelper.GetDecoratedName(rp);
                if (!map.ContainsKey(key))
                    map[key] = value;
            }

            // longer names first to avoid partial replacement
            var ordered = map.OrderByDescending(kv => kv.Key.Length).ToList();
            return ordered.Aggregate(input, (current, kv) => current.Replace(kv.Key, kv.Value));
        }
    }

    public static Pawn GetHostilePawnNearBy(this Pawn pawn)
    {
        if (pawn?.Map == null) return null;

        // 1. Choose a faction
        Faction referenceFaction;

        if (pawn.IsPrisoner || pawn.IsSlave || pawn.IsFreeColonist || pawn.IsVisitor() || pawn.IsQuestLodger())
        {
            // Prisoners, colonists, use player faction
            referenceFaction = Faction.OfPlayer;
        }
        else
        {
            // enemies, wildmans, use own faction
            referenceFaction = pawn.Faction;
        }

        if (referenceFaction == null) return null;

        var hostileTargets = pawn.Map.attackTargetsCache?.TargetsHostileToFaction(referenceFaction);

        if (hostileTargets == null) return null;

        Pawn closestPawn = null;
        float closestDistSq = float.MaxValue;

        foreach (var target in hostileTargets.Where(target => GenHostility.IsActiveThreatTo(target, referenceFaction)))
        {

            if (target.Thing is not Pawn threatPawn) continue;
            if (threatPawn.Downed) continue;
            
            // --- 2. filter hostile ---

            // a. filter normal colonist
            if (threatPawn.IsPrisoner && threatPawn.HostFaction == Faction.OfPlayer)
                continue;
            if (threatPawn.IsSlave && threatPawn.HostFaction == Faction.OfPlayer)
                continue;

            // b. filter normal prisoner
            if (pawn.IsPrisoner && threatPawn.IsPrisoner)
                continue;

            Lord lord = threatPawn.GetLord();

            // === 1. EXCLUDE TACTICALLY RETREATING PAWNS ===
            if (lord != null && lord.CurLordToil is LordToil_ExitMapFighting or LordToil_ExitMap)
                continue;

            // === 2. EXCLUDE ROAMING MECH CLUSTER PAWNS ===
            if (threatPawn.RaceProps.IsMechanoid && lord is { CurLordToil: LordToil_DefendPoint })
                continue;

            // === 3. CALCULATE DISTANCE FOR VALID THREATS ===
            float distSq = pawn.Position.DistanceToSquared(threatPawn.Position);

            if (distSq < closestDistSq)
            {
                closestDistSq = distSq;
                closestPawn = threatPawn;
            }
        }

        return closestPawn;
    }

    // Using a HashSet for better readability and maintainability.
    private static readonly HashSet<string> ResearchJobDefNames =
    [
        "Research",
        // MOD: Research Reinvented
        "RR_Analyse",
        "RR_AnalyseInPlace",
        "RR_AnalyseTerrain",
        "RR_Research",
        "RR_InterrogatePrisoner",
        "RR_LearnRemotely"
    ];

    private static string GetActivity(this Pawn pawn)
    {
        if (pawn == null) return null;
        if (pawn.InMentalState)
            return pawn.MentalState?.InspectLine;

        if (pawn.CurJobDef is null)
            return null;

        var target = pawn.IsAttacking() ? pawn.TargetCurrentlyAimingAt.Thing?.LabelShortCap : null;
        if (target != null)
            return $"正在攻击 {target}";

        var lord = pawn.GetLord()?.LordJob?.GetReport(pawn);
        var job = pawn.jobs?.curDriver?.GetReport();

        string activity;
        if (lord == null) activity = job;
        else activity = job == null ? lord : $"{lord} ({job})";

        if (ResearchJobDefNames.Contains(pawn.CurJob?.def.defName))
        {
            ResearchProjectDef project = Find.ResearchManager.GetProject();
            if (project != null)
            {
                float progress = Find.ResearchManager.GetProgress(project);
                float percentage = (progress / project.baseCost) * 100f;
                activity += $" (研究项目: {project.label} - {percentage:F0}%)";
            }
        }

        return activity;
    }
    // NEW: recursively collect all pawn targets from the given job
    private static void AddJobTargetsToRelevantPawns(Job job, List<Pawn> relevantPawns)
    {
        if (job == null) return;

        var targetIndices = new List<TargetIndex>();
        foreach (TargetIndex ind in Enum.GetValues(typeof(TargetIndex)))
        {
            try
            {
                if (job.GetTarget(ind) != (LocalTargetInfo)(Thing)null)
                    targetIndices.Add(ind);
            }
            catch
            {
                // ignore invalid indices
            }
        }

        foreach (var target in targetIndices.Select(job.GetTarget))
        {
            if (target.HasThing && target.Thing is Pawn pawn && !relevantPawns.Contains(pawn))
            {
                relevantPawns.Add(pawn);
                if (pawn.CurJob != null)
                {
                    AddJobTargetsToRelevantPawns(pawn.CurJob, relevantPawns);
                }
            }
        }
    }
    public static MapRole GetMapRole(this Pawn pawn)
    {
        if (pawn?.Map == null || pawn.IsPrisonerOfColony)
            return MapRole.None;

        Map map = pawn.Map;
        Faction mapFaction = map.ParentFaction;


        if (pawn.Faction.HostileTo(mapFaction))
            return MapRole.Invading;
            
        if (mapFaction == pawn.Faction || map.IsPlayerHome)
            return MapRole.Defending; // player colonist
            
        return MapRole.Visiting; // friendly trader or visitor
    }

    public static string GetPrisonerSlaveStatus(this Pawn pawn)
    {
        if (pawn == null) return null;

        string result = "";

        if (pawn.IsPrisoner)
        {
            // === Resistance (for recruitment) ===
            float resistance = pawn.guest.resistance;
            result += $"抵抗: {resistance:0.0} ({Describer.Resistance(resistance)})\n";

            // === Will (for enslavement) ===
            float will = pawn.guest.will;
            result += $"意志: {will:0.0} ({Describer.Will(will)})\n";
        }

        // === Suppression (slave compliance, if applicable) ===
        else if (pawn.IsSlave)
        {
            var suppressionNeed = pawn.needs?.TryGetNeed<Need_Suppression>();
            if (suppressionNeed != null)
            {
                float suppression = suppressionNeed.CurLevelPercentage * 100f;
                result += $"压制率: {suppression:0.0}% ({Describer.Suppression(suppression)})\n";
            }
        }

        return result.TrimEnd();
    }

    public static bool IsPlayer(this Pawn pawn)
    {
        return pawn.LabelShort == "超凡智能";
        return pawn == Cache.GetPlayer();
    }

    public static bool HasVocalLink(this Pawn pawn)
    {
        return pawn.health.hediffSet.HasHediff(Constant.VocalLinkDef);
    }
}