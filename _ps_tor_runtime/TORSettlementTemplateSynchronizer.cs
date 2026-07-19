using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;

namespace PlayerSettlementTORRuntime
{
    public sealed class SubModule : MBSubModuleBase
    {
        private bool _registered;
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (!(game.GameType is Campaign) || _registered) return;
            _registered = true;
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }
        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try { TORSettlementTemplateSynchronizer.Synchronize(); }
            catch (Exception ex) { TaleWorlds.Library.Debug.Print("[PlayerSettlementTORRuntime] Town template synchronization failed: " + ex); }
        }
    }

    internal static class TORSettlementTemplateSynchronizer
    {
        private const string PlayerSettlementModule = "PlayerSettlement";
        private const string TorTemplateModule = "PlayerSettlement_TOR";
        private const string TorCoreModule = "TOR_Core";
        private const string Marker = "_ToR";

        internal static void Synchronize()
        {
            var torModule = ModuleHelper.GetModuleInfo(TorCoreModule);
            if (torModule == null || string.IsNullOrEmpty(torModule.FolderPath)) return;
            string settlementsPath = Path.Combine(torModule.FolderPath, "ModuleData", "tor_settlements.xml");
            if (!File.Exists(settlementsPath)) return;
            var torSettlements = new XmlDocument(); torSettlements.Load(settlementsPath);
            var townsByCulture = ReadTorTowns(torSettlements);
            if (townsByCulture.Count == 0) return;
            Assembly ps = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, PlayerSettlementModule, StringComparison.OrdinalIgnoreCase));
            if (ps == null) return;
            Type mainType = ps.GetType("BannerlordPlayerSettlement.Main", false); if (mainType == null) return;
            object submodule = mainType.GetField("Submodule", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); if (submodule == null) return;
            IDictionary templates = mainType.GetField("CultureTemplates", BindingFlags.Public | BindingFlags.Instance)?.GetValue(submodule) as IDictionary; if (templates == null) return;
            Type templateType = ps.GetType("BannerlordPlayerSettlement.Descriptors.CultureSettlementTemplate", true);
            Type listType = typeof(List<>).MakeGenericType(templateType);
            foreach (var entry in townsByCulture)
            {
                string cultureId = entry.Key; List<XmlElement> sourceTowns = entry.Value; if (sourceTowns.Count == 0) continue;
                IList list = templates.Contains(cultureId) ? templates[cultureId] as IList : null;
                if (list == null) { list = (IList)Activator.CreateInstance(listType); templates[cultureId] = list; }
                RemoveExistingTorTownTemplates(list);
                XmlDocument generated = BuildTemplateDocument(cultureId, sourceTowns);
                object template = Activator.CreateInstance(templateType);
                SetField(templateType, template, "FromModule", TorTemplateModule);
                SetField(templateType, template, "TemplateModifier", Marker + "_runtime");
                SetField(templateType, template, "Document", generated);
                SetField(templateType, template, "CultureId", cultureId);
                list.Add(template);
            }
        }

        private static Dictionary<string, List<XmlElement>> ReadTorTowns(XmlDocument document)
        {
            var result = new Dictionary<string, List<XmlElement>>(StringComparer.OrdinalIgnoreCase);
            XmlNodeList nodes = document.SelectNodes("//Settlement"); if (nodes == null) return result;
            foreach (XmlNode node in nodes)
            {
                var settlement = node as XmlElement; if (settlement == null) continue;
                var town = settlement.SelectSingleNode("./Components/Town") as XmlElement; if (town == null || IsCastle(town)) continue;
                string culture = NormalizeReference(settlement.GetAttribute("culture"), "Culture."); if (string.IsNullOrEmpty(culture)) continue;
                if (!result.TryGetValue(culture, out List<XmlElement> list)) { list = new List<XmlElement>(); result[culture] = list; }
                list.Add(settlement);
            }
            return result;
        }
        private static bool IsCastle(XmlElement town) { string v = town.GetAttribute("is_castle"); return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1"; }
        private static string NormalizeReference(string v, string prefix) { if (string.IsNullOrWhiteSpace(v)) return string.Empty; v=v.Trim(); return v.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?v.Substring(prefix.Length):v; }

        private static void RemoveExistingTorTownTemplates(IList templateList)
        {
            foreach (object template in templateList)
            {
                if (template == null) continue; Type type=template.GetType();
                string from=type.GetField("FromModule")?.GetValue(template) as string;
                if (!string.Equals(from,TorTemplateModule,StringComparison.OrdinalIgnoreCase)) continue;
                XmlDocument doc=type.GetField("Document")?.GetValue(template) as XmlDocument; if(doc==null) continue;
                XmlNodeList towns=doc.SelectNodes("//Settlement[@template_type='Town']"); if(towns==null) continue;
                foreach(XmlNode n in towns.Cast<XmlNode>().ToList()) n.ParentNode?.RemoveChild(n);
                XmlElement root=doc.DocumentElement; if(root!=null && root.HasAttribute("towns")) root.SetAttribute("towns","0");
            }
        }

        private static XmlDocument BuildTemplateDocument(string cultureId, IList<XmlElement> sourceTowns)
        {
            var output=new XmlDocument(); XmlElement root=output.CreateElement("Settlements"); output.AppendChild(root);
            root.SetAttribute("towns",sourceTowns.Count.ToString()); root.SetAttribute("castles","0"); root.SetAttribute("villages","0"); root.SetAttribute("culture_template",cultureId); root.SetAttribute("template_modifier",Marker+"_runtime");
            for(int i=0;i<sourceTowns.Count;i++){ XmlElement clone=(XmlElement)output.ImportNode(sourceTowns[i],true); PrepareTownTemplate(clone,cultureId,i+1); root.AppendChild(clone); }
            return output;
        }

        private static void PrepareTownTemplate(XmlElement settlement,string cultureId,int variant)
        {
            string id="player_settlement_town_"+Sanitize(cultureId)+"_variant_"+variant+Marker+"_runtime";
            settlement.SetAttribute("id",id); settlement.SetAttribute("name","{=player_settlement_n_01}Player Settlement"); settlement.SetAttribute("owner","Faction.{{PLAYER_CLAN}}"); settlement.SetAttribute("posX","{{POS_X}}"); settlement.SetAttribute("posY","{{POS_Y}}"); settlement.SetAttribute("culture","Culture.{{PLAYER_CULTURE}}"); settlement.SetAttribute("gate_posX","{{G_POS_X}}"); settlement.SetAttribute("gate_posY","{{G_POS_Y}}"); settlement.SetAttribute("template_type","Town"); settlement.SetAttribute("template_variant",variant.ToString()); settlement.RemoveAttribute("prefab_id");
            var town=settlement.SelectSingleNode("./Components/Town") as XmlElement; if(town!=null){town.SetAttribute("id",id+"_town_comp");town.SetAttribute("is_castle","false");}
        }
        private static string Sanitize(string value){if(string.IsNullOrEmpty(value))return "tor";return new string(value.Select(ch=>char.IsLetterOrDigit(ch)||ch=='_'?ch:'_').ToArray());}
        private static void SetField(Type type,object target,string name,object value){FieldInfo f=type.GetField(name,BindingFlags.Public|BindingFlags.Instance);if(f==null)throw new MissingFieldException(type.FullName,name);f.SetValue(target,value);}
    }
}
