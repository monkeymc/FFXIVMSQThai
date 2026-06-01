using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace FfxivMsqThai.Services;

public sealed record QuestInfo(ushort Id, string Name, string Slug);

/// <summary>
/// Detects the currently active quest by reading game memory, mirroring the logic in
/// Questionable's <c>QuestFunctions.GetMainScenarioQuest</c>.
///
/// NG+:      QuestRedoHud addon AtkValues[0] state check + agent offset +46.
/// Primary:  AgentScenarioTree->Data->MainScenarioQuestIds[0].
/// Fallback: first tracked normal quest from QuestManager->TrackedQuests.
/// </summary>
public sealed class QuestDetector
{
    private readonly IDataManager _dataManager;
    private readonly IGameGui     _gameGui;
    private readonly IPluginLog   _log;
    private readonly Dictionary<ushort, QuestInfo?> _cache = new();
    public string? ForcedQuestSlug { get; set; }

    public QuestDetector(IDataManager dataManager, IGameGui gameGui, IPluginLog log)
    {
        _dataManager = dataManager;
        _gameGui     = gameGui;
        _log         = log;
    }

    public unsafe QuestInfo? GetCurrentQuestInfo() => GetCurrentQuestInfo(verbose: false);

    public unsafe QuestInfo? GetCurrentQuestInfo(bool verbose)
    {
        if (!string.IsNullOrEmpty(ForcedQuestSlug))
        {
            if (verbose) _log.Information($"[MSQ-Thai-Quest] Forced JSON override active: {ForcedQuestSlug}");
            return new QuestInfo(0, "Forced Quest", ForcedQuestSlug);
        }

        try
        {
            // ── NG+ ───────────────────────────────────────────────────────────
            // "Memories Rekindled" (3759) is the last EW quest.
            // When NG+ chapter is active, AgentScenarioTree still holds the player's
            // real MSQ progress (e.g. DT), NOT the NG+ replay quest. So when the
            // QuestRedoHud is active we skip AgentScenarioTree entirely and read the
            // NG+ quest from either the agent offset +46 or QuestManager TrackedQuests.
            var agentModule = AgentModule.Instance();
            if (QuestManager.IsQuestComplete(3759))
            {
                var redoHud     = agentModule != null
                    ? agentModule->GetAgentByInternalId(AgentId.QuestRedoHud)
                    : null;

                if (redoHud != null && redoHud->IsAgentActive())
                {
                    var addonPtr  = _gameGui.GetAddonByName("QuestRedoHud");
                    var addon     = (AtkUnitBase*)addonPtr.Address;
                    var state     = addon != null && addon->AtkValuesCount >= 1
                        ? addon->AtkValues[0].UInt
                        : uint.MaxValue;
                    // state 0/2/3 = chapter actively running → player is doing NG+ content
                    // state 1     = chapter paused          → player doing normal MSQ
                    // state 4     = just turned in quest    → transitioning; treat as normal
                    bool chapterRunning = state is 0 or 2 or 3;

                    ushort ngpId = MemoryHelper.Read<ushort>((nint)redoHud + 46);

                    if (verbose)
                        _log.Information(
                            $"[MSQ-Thai-Quest] QuestRedoHud active  state={state}  chapterRunning={chapterRunning}  questId(+46)={ngpId}");

                    if (chapterRunning)
                    {
                        // +46 has the quest ID when in an active step; may be 0 while walking.
                        if (ngpId != 0)
                        {
                            var info = Resolve(ngpId);
                            if (info != null) return info;
                            if (verbose) _log.Warning($"[MSQ-Thai-Quest] NG+ Resolve({ngpId}) returned null");
                        }

                        // +46 = 0 (idle between steps): use TrackedQuests to find NG+ quest.
                        // Skip AgentScenarioTree — it returns the player's real MSQ progress,
                        // not the NG+ chapter quest.
                        if (verbose) _log.Information("[MSQ-Thai-Quest] NG+ chapter running — using TrackedQuests");
                        goto trackedQuests;
                    }
                }
                else if (verbose)
                {
                    _log.Information("[MSQ-Thai-Quest] NG+ eligible but QuestRedoHud not active");
                }
            }

            // ── Unending Journey (AgentArchive) ──────────────────────────────────────
            var archiveAgent = agentModule != null ? agentModule->GetAgentByInternalId(AgentId.ArchiveItem) : null;
            if (archiveAgent != null && archiveAgent->IsAgentActive())
            {
                // ใน FFXIVClientStructs ค่า Quest ID ของ UJ มักจะถูกเก็บเป็นตัวแปรใน Struct ของ Agent 
                // หรือสามารถดึงได้จาก Event ID ปัจจุบัน
                // (หมายเหตุ: Offset อาจต้องใช้ ReClass.NET ส่องดูอีกครั้ง แต่ปกติจะอยู่ราวๆ +0x28 หรือ +0x30)
                ushort ujQuestId = MemoryHelper.Read<ushort>((nint)archiveAgent + 0x28);

                if (ujQuestId != 0)
                {
                    var info = Resolve(ujQuestId);
                    if (info != null) return info;
                }
            }

            // ── AgentScenarioTree (normal / NG+ paused) ───────────────────────
            AgentScenarioTree* scenarioTree = AgentScenarioTree.Instance();
            if (scenarioTree == null)
            {
                if (verbose) _log.Information("[MSQ-Thai-Quest] AgentScenarioTree.Instance() = null");
            }
            else if (scenarioTree->Data == null)
            {
                if (verbose) _log.Information("[MSQ-Thai-Quest] AgentScenarioTree->Data = null");
            }
            else
            {
                ushort msqId = scenarioTree->Data->MainScenarioQuestIds[0];
                if (verbose) _log.Information($"[MSQ-Thai-Quest] AgentScenarioTree MainScenarioQuestIds[0] = {msqId}");
                if (msqId != 0)
                {
                    var info = Resolve(msqId);
                    if (info != null) return info;
                    if (verbose) _log.Warning($"[MSQ-Thai-Quest] Resolve({msqId}) returned null");
                }
            }

            // ── QuestManager tracked quests (NG+ chapter + non-MSQ fallback) ──
            trackedQuests:
            QuestManager* qm = QuestManager.Instance();
            if (qm == null)
            {
                if (verbose) _log.Information("[MSQ-Thai-Quest] QuestManager.Instance() = null");
            }
            else
            {
                int tracked = 0;
                for (int i = qm->TrackedQuests.Length - 1; i >= 0; i--)
                {
                    TrackingWork tq = qm->TrackedQuests[i];
                    if (tq.QuestType != 1) continue;
                    tracked++;
                    var qId  = qm->NormalQuests[tq.Index].QuestId;
                    var info = Resolve(qId);
                    if (verbose) _log.Information($"[MSQ-Thai-Quest] TrackedQuest[{i}] type=1 questId={qId} → {(info != null ? $"'{info.Name}'" : "null")}");
                    if (info != null) return info;
                }
                if (verbose && tracked == 0)
                    _log.Information("[MSQ-Thai-Quest] QuestManager: no tracked quests of type=1 found");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[MSQ-Thai] QuestDetector: {ex.Message}");
        }

        return null;
    }

    private QuestInfo? Resolve(ushort questId)
    {
        if (questId == 0) return null;
        if (_cache.TryGetValue(questId, out var cached)) return cached;

        try
        {
            // Game memory stores quest IDs as (rowId - 0x10000); add the base back.
            var rowId = (uint)questId | 0x10000u;
            var row   = _dataManager.GetExcelSheet<Quest>().GetRow(rowId);
            var name  = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                _cache[questId] = null;
                return null;
            }
            var info = new QuestInfo(questId, name, DialogueDictionary.ToPureAlphanumericKey(name));
            _cache[questId] = info;
            return info;
        }
        catch (Exception ex)
        {
            _log.Warning($"[MSQ-Thai-Quest] Resolve({questId}) exception: {ex.GetType().Name}: {ex.Message}");
            _cache[questId] = null;
            return null;
        }
    }
}
