using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace PlayerSettlementTORRuntime
{
    /// <summary>
    /// Synchronizes Player Settlement's selectable TOR town templates with the
    /// actual settlements loaded by the active TOR campaign. This runs once at
    /// campaign startup; it does not add campaign ticks, scans, or recurring work.
    /// </summary>
    public sealed class TorRuntimeTemplateSubModule : MBSubModuleBase
    {
        private const string PlayerSettlementAssemblyName = "PlayerSettlement";
        private const string PlayerSettlementMainTypeName = "BannerlordPlayerSettlement.Main";
        private const string CultureTemplateTypeName = "BannerlordPlayerSettlement.Descriptors.CultureSettlementTemplate";
        private const string RuntimeTemplateModifier = "_ToR_runtime";

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);

            if (!(game.GameType is Campaign))
                return;

            try
            {
                SynchronizeTownTemplates();
            }
            catch (Exception ex)
            {
                Debug.Print("[PlayerSettlementTORRuntime] Failed to synchronize TOR town templates: " + ex, 0, Debug.DebugColor.Red);
            }
        }

        private static void SynchronizeTownTemplates()
        {
            Assembly playerSettlementAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, PlayerSettlementAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (playerSettlementAssembly == null)
            {
                Log("PlayerSettlement assembly is not loaded; runtime TOR synchronization skipped.");
                return;
            }

            Type mainType = playerSettlementAssembly.GetType(PlayerSettlementMainTypeName, false);
            Type cultureTemplateType = playerSettlementAssembly.GetType(CultureTemplateTypeName, false);
            if (mainType == null || cultureTemplateType == null)
                throw new InvalidOperationException("PlayerSettlement runtime types could not be resolved.");

            FieldInfo submoduleField = mainType.GetField("Submodule", BindingFlags.Public | BindingFlags.Static);
            object submodule = submoduleField?.GetValue(null);
            if (submodule == null)
                throw new InvalidOperationException("PlayerSettlement.Main.Submodule is not initialized.");

            FieldInfo cultureTemplatesField = mainType.GetField("CultureTemplates", BindingFlags.Public | BindingFlags.Instance);
            IDictionary cultureTemplates = cultureTemplatesField?.GetValue(submodule) as IDictionary;
            if (cultureTemplates == null)
                throw new InvalidOperationException("PlayerSettlement culture template dictionary is not initialized.");

            string settlementsPath = FindTorSettlementsPath();
            if (string.IsNullOrEmpty(settlementsPath))
                throw new FileNotFoundException("TOR_Core/ModuleData/tor_settlements.xml could not be located.");

            var torDocument = new XmlDocument();
            torDocument.PreserveWhitespace = true;
            torDocument.Load(settlementsPath);

            Dictionary<string, XmlNode> sourceNodesById = BuildSourceNodeIndex(torDocument);
            Dictionary<string, List<XmlNode>> townsByCulture = BuildLoadedTownGroups(sourceNodesById);
            if (townsByCulture.Count == 0)
                throw new InvalidOperationException("No visible TOR towns could be matched to tor_settlements.xml.");

            int totalTowns = 0;
            foreach (KeyValuePair<string, List<XmlNode>> pair in townsByCulture)
            {
                string cultureId = pair.Key;
                List<XmlNode> sourceTownNodes = pair.Value;
                if (sourceTownNodes.Count == 0)
                    continue;

                IList cultureTemplateList = GetOrCreateCultureTemplateList(cultureTemplates, cultureId, cultureTemplateType);

                // Remove town records from all previously loaded template documents for this culture.
                // Castle and village records remain untouched. This eliminates the inconsistent static
                // subset and prevents duplicate vanilla/legacy town choices from leaking into TOR.
                foreach (object existingTemplate in cultureTemplateList.Cast<object>().ToList())
                    RemoveTownNodes(existingTemplate, cultureTemplateType);

                XmlDocument runtimeDocument = BuildRuntimeTemplateDocument(cultureId, sourceTownNodes);
                object runtimeTemplate = Activator.CreateInstance(cultureTemplateType);
                SetField(cultureTemplateType, runtimeTemplate, "FromModule", "TOR_Core");
                SetField(cultureTemplateType, runtimeTemplate, "TemplateModifier", RuntimeTemplateModifier);
                SetField(cultureTemplateType, runtimeTemplate, "Document", runtimeDocument);
                SetField(cultureTemplateType, runtimeTemplate, "CultureId", cultureId);
                cultureTemplateList.Add(runtimeTemplate);

                totalTowns += sourceTownNodes.Count;
                Log("Synchronized " + sourceTownNodes.Count + " town template(s) for culture '" + cultureId + "'.");
            }

            Log("TOR town template synchronization complete: " + totalTowns + " loaded town(s) across " + townsByCulture.Count + " culture(s).");
        }

        private static Dictionary<string, XmlNode> BuildSourceNodeIndex(XmlDocument document)
        {
            var result = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);
            XmlNodeList nodes = document.SelectNodes("//Settlement");
            if (nodes == null)
                return result;

            foreach (XmlNode node in nodes)
            {
                string id = node.Attributes?["id"]?.Value;
                if (!string.IsNullOrEmpty(id) && !result.ContainsKey(id))
                    result.Add(id, node);
            }
            return result;
        }

        private static Dictionary<string, List<XmlNode>> BuildLoadedTownGroups(Dictionary<string, XmlNode> sourceNodesById)
        {
            var result = new Dictionary<string, List<XmlNode>>(StringComparer.OrdinalIgnoreCase);

            // Use Settlement.All order deliberately. PlayerSettlementCultureVisuals selects the
            // Nth same-culture live town for _variant_N_ToR identifiers, so matching this order
            // makes preview and persisted campaign-map visuals resolve to the exact source town.
            foreach (Settlement settlement in Settlement.All)
            {
                if (settlement == null || !settlement.IsTown || !settlement.IsVisible || settlement.Culture == null)
                    continue;
                if (string.IsNullOrEmpty(settlement.StringId) || settlement.StringId.StartsWith("player_settlement_", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!sourceNodesById.TryGetValue(settlement.StringId, out XmlNode sourceNode))
                {
                    Log("Loaded TOR town '" + settlement.StringId + "' has no matching XML node; skipped.");
                    continue;
                }

                string cultureId = settlement.Culture.StringId;
                if (string.IsNullOrEmpty(cultureId))
                    continue;

                if (!result.TryGetValue(cultureId, out List<XmlNode> towns))
                {
                    towns = new List<XmlNode>();
                    result.Add(cultureId, towns);
                }
                towns.Add(sourceNode);
            }

            return result;
        }

        private static XmlDocument BuildRuntimeTemplateDocument(string cultureId, List<XmlNode> sourceTownNodes)
        {
            var document = new XmlDocument();
            XmlElement root = document.CreateElement("Settlements");
            root.SetAttribute("culture_template", cultureId);
            root.SetAttribute("template_modifier", RuntimeTemplateModifier);
            document.AppendChild(root);

            for (int i = 0; i < sourceTownNodes.Count; i++)
            {
                XmlElement clone = (XmlElement)document.ImportNode(sourceTownNodes[i], true);
                string sourceId = clone.GetAttribute("id");
                int variant = i + 1;
                string safeSourceId = SanitizeIdentifier(sourceId);
                string templateId = "player_settlement_town_" + cultureId + "_variant_" + variant + "_ToR_source_" + safeSourceId;

                clone.SetAttribute("id", templateId);
                clone.SetAttribute("template_type", "Town");
                clone.SetAttribute("template_variant", variant.ToString());
                clone.RemoveAttribute("prefab_id");

                // Player Settlement replaces these placeholders at creation. Keeping source-map
                // absolute gate coordinates would otherwise point the new town back at the source.
                clone.SetAttribute("gate_posX", "{{G_POS_X}}");
                clone.SetAttribute("gate_posY", "{{G_POS_Y}}");

                XmlNode townComponent = clone.SelectSingleNode("descendant::Town");
                if (townComponent == null)
                    throw new InvalidDataException("TOR town '" + sourceId + "' has no Town component.");
                SetNodeAttribute(townComponent, "id", templateId + "_town_comp");

                root.AppendChild(clone);
            }

            return document;
        }

        private static IList GetOrCreateCultureTemplateList(IDictionary dictionary, string cultureId, Type cultureTemplateType)
        {
            if (dictionary.Contains(cultureId))
                return (IList)dictionary[cultureId];

            Type listType = typeof(List<>).MakeGenericType(cultureTemplateType);
            IList list = (IList)Activator.CreateInstance(listType);
            dictionary.Add(cultureId, list);
            return list;
        }

        private static void RemoveTownNodes(object cultureTemplate, Type cultureTemplateType)
        {
            FieldInfo documentField = cultureTemplateType.GetField("Document", BindingFlags.Public | BindingFlags.Instance);
            XmlDocument document = documentField?.GetValue(cultureTemplate) as XmlDocument;
            if (document == null)
                return;

            XmlNodeList townNodes = document.SelectNodes("//Settlement[@template_type='Town']");
            if (townNodes == null || townNodes.Count == 0)
                return;

            foreach (XmlNode node in townNodes.Cast<XmlNode>().ToList())
                node.ParentNode?.RemoveChild(node);
        }

        private static string FindTorSettlementsPath()
        {
            var starts = new List<string>();
            if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
                starts.Add(AppDomain.CurrentDomain.BaseDirectory);
            if (!string.IsNullOrEmpty(Environment.CurrentDirectory))
                starts.Add(Environment.CurrentDirectory);

            foreach (string start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DirectoryInfo current;
                try { current = new DirectoryInfo(Path.GetFullPath(start)); }
                catch { continue; }

                for (int depth = 0; current != null && depth < 7; depth++, current = current.Parent)
                {
                    string direct = Path.Combine(current.FullName, "Modules", "TOR_Core", "ModuleData", "tor_settlements.xml");
                    if (File.Exists(direct))
                        return direct;

                    string sibling = Path.Combine(current.FullName, "TOR_Core", "ModuleData", "tor_settlements.xml");
                    if (File.Exists(sibling))
                        return sibling;
                }
            }

            return null;
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
                throw new MissingFieldException(type.FullName, name);
            field.SetValue(target, value);
        }

        private static void SetNodeAttribute(XmlNode node, string name, string value)
        {
            XmlAttribute attribute = node.Attributes?[name];
            if (attribute == null)
            {
                attribute = node.OwnerDocument.CreateAttribute(name);
                node.Attributes.Append(attribute);
            }
            attribute.Value = value;
        }

        private static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            return new string(value.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        }

        private static void Log(string message)
        {
            Debug.Print("[PlayerSettlementTORRuntime] " + message, 0, Debug.DebugColor.Green);
        }
    }
}
