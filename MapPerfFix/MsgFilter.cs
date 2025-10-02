using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace MapPerfProbe
{
    internal static class MsgFilter
    {
        internal static int FilterCount;

        private static readonly string[] Bypass =
        {
            "your settlement is under attack",
            "quest failed",
            "low food",
            "deine siedlung wird angegriffen",
            "auftrag fehlgeschlagen",
            "niedrige vorräte"
        };

        private static readonly HashSet<string> WordBoundaryTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "quest",
                "skill",
                "perk",
                "trait",
                "clan",
                "policy",
                "klan",
                "siege",
                "prices",
                "market",
                "caravan",
                "relation",
                "kingdom"
            };

        private const int FamilyRaids = 1 << 0;
        private const int FamilySieges = 1 << 1;
        private const int FamilyWarPeace = 1 << 2;
        private const int FamilyArmiesParties = 1 << 3;
        private const int FamilyEconomy = 1 << 4;
        private const int FamilySettlements = 1 << 5;
        private const int FamilyQuests = 1 << 6;
        private const int FamilySkillsTraits = 1 << 7;
        private const int FamilyClanKingdom = 1 << 8;

        // Simple, fast, case-insensitive substring families. EN + DE seeds.
        private static readonly string[][] FamilyTokens =
        {
            new[] { "is raiding", "are raiding", "raided", "raiding", "plündert", "überfällt" },
            new[] { "is besieging", "are besieging", "siege", "belagert", "belagerung" },
            new[] { "declared war", "made peace", "war has been declared", "frieden geschlossen", "krieg erklärt" },
            new[] { "created an army", "joined the army", "army disbanded", "party spotted", "spotted near", "armee", "heer" },
            new[] { "caravan", "trade rumor", "prices", "market", "karawane", "handel", "preise" },
            new[] { "under attack", "has been taken", "captured", "sacked", "angegriffen", "eingenommen", "geplündert" },
            new[] { "quest", "auftrag", "mission", "quest completed", "quest failed", "auftrag fehlgeschlagen" },
            new[] { "skill", "perk", "trait", "fertigkeit", "talent", "eigenschaft" },
            new[] { "clan", "kingdom", "policy", "relation", "klan", "königreich", "politik", "beziehung" }
        };

        private const int DedupInitialCapacity = 32;
        private static readonly ConstructorInfo HashSetWithCapacityCtor =
            typeof(HashSet<string>).GetConstructor(new[] { typeof(int), typeof(IEqualityComparer<string>) });

        private static string _customCacheRaw = string.Empty;
        private static string[] _customCacheTokens = Array.Empty<string>();

        private static volatile int _frameFamilyMask;
        private static int _activeFamilyMask = 0;
        private static string[] _activeFamilyTokens = Array.Empty<string>();

        internal static bool ShouldBlock(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var s = text.Trim();

            for (int i = 0; i < Bypass.Length; i++)
            {
                if (s.IndexOf(Bypass[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            // Custom user patterns
            var customTokens = GetCustomTokens();
            for (int i = 0; i < customTokens.Length; i++)
            {
                var token = customTokens[i];
                if (s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (MapPerfConfig.DebugLogging)
                        MapPerfLog.Info($"[filter/custom] {token} :: {s}");
                    Interlocked.Increment(ref FilterCount);
                    return true;
                }
            }

            var mask = GetFamilyMaskSnapshot();
            if (mask == 0)
            {
                return false;
            }

            var tokens = GetActiveFamilyTokens(mask);
            for (int i = 0; i < tokens.Length; i++)
            {
                var needle = tokens[i];
                if (RequiresWordBoundary(needle))
                {
                    if (ContainsWord(s, needle))
                    {
                        Interlocked.Increment(ref FilterCount);
                        return true;
                    }
                }
                else if (s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Interlocked.Increment(ref FilterCount);
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RequiresWordBoundary(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            if (token.IndexOf(' ') >= 0)
                return false;

            return WordBoundaryTokens.Contains(token);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsWord(string haystack, string needle)
        {
            var index = 0;
            var needleLength = needle.Length;
            while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var leftOk = index == 0 || !char.IsLetter(haystack[index - 1]);
                var end = index + needleLength;
                var rightOk = end >= haystack.Length ||
                              !char.IsLetter(haystack[end]) ||
                              haystack[end] == '\'' ||
                              (haystack[end] == 's' &&
                               (end + 1 >= haystack.Length || !char.IsLetter(haystack[end + 1])));
                if (leftOk && rightOk)
                    return true;

                index += 1;
            }
            return false;
        }

        private static string[] GetCustomTokens()
        {
            var raw = MapPerfConfig.CustomPatterns;
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            var cachedRaw = Volatile.Read(ref _customCacheRaw);
            var cachedTokens = Volatile.Read(ref _customCacheTokens);
            if (string.Equals(raw, cachedRaw, StringComparison.Ordinal))
                return cachedTokens;

            var split = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0)
            {
                Volatile.Write(ref _customCacheTokens, Array.Empty<string>());
                Volatile.Write(ref _customCacheRaw, raw);
                return Array.Empty<string>();
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokens = new List<string>(split.Length);
            for (int i = 0; i < split.Length; i++)
            {
                var t = split[i]?.Trim();
                if (string.IsNullOrEmpty(t))
                    continue;

                if (t.Length > 1 && set.Add(t))
                    tokens.Add(t);
            }

            var arr = tokens.Count == 0 ? Array.Empty<string>() : tokens.ToArray();
            Volatile.Write(ref _customCacheTokens, arr);
            Volatile.Write(ref _customCacheRaw, raw);
            return arr;
        }

        internal static void RefreshFamilyMaskFromConfig()
        {
            var mask = 0;
            if (MapPerfConfig.F_Raids) mask |= FamilyRaids;
            if (MapPerfConfig.F_Sieges) mask |= FamilySieges;
            if (MapPerfConfig.F_WarPeace) mask |= FamilyWarPeace;
            if (MapPerfConfig.F_ArmiesParties) mask |= FamilyArmiesParties;
            if (MapPerfConfig.F_Economy) mask |= FamilyEconomy;
            if (MapPerfConfig.F_Settlements) mask |= FamilySettlements;
            if (MapPerfConfig.F_Quests) mask |= FamilyQuests;
            if (MapPerfConfig.F_SkillsTraits) mask |= FamilySkillsTraits;
            if (MapPerfConfig.F_ClanKingdom) mask |= FamilyClanKingdom;

            var previous = Volatile.Read(ref _frameFamilyMask);
            if (mask != previous)
                Volatile.Write(ref _frameFamilyMask, mask);
        }

        private static int GetFamilyMaskSnapshot()
        {
            return Volatile.Read(ref _frameFamilyMask);
        }

        private static string[] GetActiveFamilyTokens(int mask)
        {
            var cachedMask = Volatile.Read(ref _activeFamilyMask);
            var cachedTokens = Volatile.Read(ref _activeFamilyTokens);
            if (mask == cachedMask)
                return cachedTokens;

            if (mask == 0)
                return Array.Empty<string>();

            var list = new List<string>(32);
            HashSet<string> dedup = null;
            for (int i = 0; i < FamilyTokens.Length; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;

                var arr = FamilyTokens[i];
                for (int j = 0; j < arr.Length; j++)
                {
                    var token = arr[j];
                    if (string.IsNullOrEmpty(token))
                        continue;

                    if (dedup == null)
                        dedup = CreateDedupSet();
                    if (dedup.Add(token))
                        list.Add(token);
                }
            }

            var result = list.Count == 0 ? Array.Empty<string>() : list.ToArray();
            Volatile.Write(ref _activeFamilyTokens, result);
            Volatile.Write(ref _activeFamilyMask, mask);
            return result;
        }

        private static HashSet<string> CreateDedupSet()
        {
            if (HashSetWithCapacityCtor != null)
            {
                var instance = HashSetWithCapacityCtor.Invoke(new object[] { DedupInitialCapacity, StringComparer.OrdinalIgnoreCase });
                if (instance is HashSet<string> set)
                    return set;
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static class IMTools
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo> PropertyCache =
            new ConcurrentDictionary<Type, PropertyInfo>();

        internal static string ExtractText(object obj)
        {
            if (obj == null)
                return string.Empty;

            var type = obj.GetType();
            PropertyInfo property;
            if (!PropertyCache.TryGetValue(type, out property))
            {
                property = ResolvePreferredProperty(type);
                if (property != null)
                    PropertyCache.TryAdd(type, property);
            }

            var value = property != null ? property.GetValue(obj) : null;
            return (value ?? obj).ToString() ?? string.Empty;
        }

        private static PropertyInfo ResolvePreferredProperty(Type type)
        {
            return type.GetProperty("Information")
                   ?? type.GetProperty("Text")
                   ?? type.GetProperty("Message");
        }
    }

    [HarmonyPatch]
    internal static class IM_DisplayMessage_Patch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var msgTypeNames = new[]
            {
                "TaleWorlds.Core.InformationMessage",
                "TaleWorlds.Library.InformationMessage"
            };
            var imTypeNames = new[]
            {
                "TaleWorlds.Core.InformationManager",
                "TaleWorlds.Library.InformationManager"
            };

            foreach (var imName in imTypeNames)
            {
                var imType = AccessTools.TypeByName(imName);
                if (imType == null)
                    continue;

                foreach (var msgName in msgTypeNames)
                {
                    var msgType = AccessTools.TypeByName(msgName);
                    if (msgType == null)
                        continue;

                    var method = AccessTools.Method(imType, "DisplayMessage", new[] { msgType });
                    if (method != null)
                        yield return method;
                }
            }
        }

        [HarmonyPriority(Priority.VeryHigh)]
        static bool Prefix(object message)
        {
            // MapPerfConfig.DebugLogging can be used to log filtered lines if desired.
            return !MsgFilter.ShouldBlock(IMTools.ExtractText(message));
        }
    }

    [HarmonyPatch]
    internal static class IM_AddQuickInformation_Patch
    {
        // Patch all overloads named AddQuickInformation; first arg is usually TextObject
        static IEnumerable<MethodBase> TargetMethods()
        {
            var imType = AccessTools.TypeByName("TaleWorlds.Core.InformationManager")
                        ?? AccessTools.TypeByName("TaleWorlds.Library.InformationManager");
            if (imType == null)
                return Enumerable.Empty<MethodBase>();
            return imType.GetMethods().Where(m => m.Name == "AddQuickInformation");
        }

        [HarmonyPriority(Priority.VeryHigh)]
        static bool Prefix(object __0)
        {
            if (__0 == null)
                return true;
            return !MsgFilter.ShouldBlock(IMTools.ExtractText(__0));
        }
    }
}

