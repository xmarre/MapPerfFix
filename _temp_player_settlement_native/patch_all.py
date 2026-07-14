from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
proj = root / 'BannerlordPlayerSettlement'

def load(rel):
    return (proj / rel).read_text(encoding='utf-8-sig')

def save(rel, text):
    (proj / rel).write_text(text, encoding='utf-8', newline='\n')

def one(text, old, new, name, count=1):
    found = text.count(old)
    if found != count:
        raise RuntimeError(f'{name}: expected {count}, found {found}')
    return text.replace(old, new, count)

s = load('Main.cs')
s = one(s,
'        private static List<ICompatibilityPatch> HarmonyCompatPatches = LoadCompatPatches().ToList();',
'''        private static readonly List<ICompatibilityPatch> HarmonyCompatPatches = new();
        private bool _runtimeInitialized;
        private bool _afterMenuPatchesApplied;''', 'static compatibility scan')
s = one(s, '            Submodule = this;\n\n            //Debugger.Launch();',
'''            Submodule = this;
            WriteStartupDiagnostic("Main constructor");

            //Debugger.Launch();''', 'constructor')
start = s.index('        protected override void OnSubModuleLoad()')
end = s.index('        protected override void OnBeforeInitialModuleScreenSetAsRoot()', start)
s = s[:start] + '''        protected override void OnSubModuleLoad()
        {
            try
            {
                WriteStartupDiagnostic("OnSubModuleLoad: enter");
                base.OnSubModuleLoad();
                WriteStartupDiagnostic("OnSubModuleLoad: complete");
            }
            catch (System.Exception e)
            {
                WriteStartupDiagnostic("OnSubModuleLoad: " + e);
            }
        }

''' + s[end:]
s = one(s, '        protected override void OnBeforeInitialModuleScreenSetAsRoot()', '        private void InitializeMenuDependentState()', 'menu method rename')
s = one(s, '        private void InitializeMenuDependentState()', '''        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            WriteStartupDiagnostic("OnBeforeInitialModuleScreenSetAsRoot");
        }

        private void InitializeMenuDependentState()''', 'minimal initial screen callback')
s = one(s, '''                    foreach (var patch in HarmonyCompatPatches)
                    {
                        patch.PatchAfterMenus(Harmony!);
                    }

                    CultureTemplates = GatherTemplates();''',
'                    CultureTemplates = GatherTemplates();', 'early after-menu patches')
old_game = '''        protected override void OnGameStart(Game game, IGameStarter starterObject)
        {
            try
            {
                base.OnGameStart(game, starterObject);

                if (game.GameType is Campaign)
                {
                    var initializer = (CampaignGameStarter) starterObject;
                    AddBehaviors(initializer);
                }
            }
            catch (System.Exception e) { LogManager.Log.NotifyBad(e); }
        }'''
new_game = '''        protected override void OnGameStart(Game game, IGameStarter starterObject)
        {
            try
            {
                WriteStartupDiagnostic("OnGameStart: enter");
                base.OnGameStart(game, starterObject);
                if (game.GameType is Campaign)
                {
                    InitializeMenuDependentState();
                    InitializeRuntimePatches();
                    AddBehaviors((CampaignGameStarter)starterObject);
                }
                WriteStartupDiagnostic("OnGameStart: complete");
            }
            catch (System.Exception e)
            {
                WriteStartupDiagnostic("OnGameStart: " + e);
                try { LogManager.Log.NotifyBad(e); } catch { }
            }
        }

        private void InitializeRuntimePatches()
        {
            if (_runtimeInitialized) return;
            _runtimeInitialized = true;
            WriteStartupDiagnostic("Runtime initialization: begin");
            Harmony = new Harmony(HarmonyDomain);
            Type[] ownTypes;
            try { ownTypes = typeof(Main).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                ownTypes = e.Types.Where(t => t != null).Cast<Type>().ToArray();
                WriteStartupDiagnostic("Own assembly partial type load: " + e);
            }
            foreach (var type in ownTypes)
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    WriteStartupDiagnostic("Harmony patch begin: " + type.FullName);
                    Harmony.CreateClassProcessor(type).Patch();
                    WriteStartupDiagnostic("Harmony patch complete: " + type.FullName);
                }
                catch (System.Exception e)
                {
                    WriteStartupDiagnostic("Harmony patch failed: " + type.FullName + " | " + e);
                    try { LogManager.Log.NotifyBad(e); } catch { }
                }
            }
            HarmonyCompatPatches.Clear();
            try { HarmonyCompatPatches.AddRange(LoadCompatPatches()); }
            catch (System.Exception e) { WriteStartupDiagnostic("Compatibility discovery failed: " + e); }
            foreach (var patch in HarmonyCompatPatches)
            {
                try { patch.PatchSubmoduleLoad(Harmony); }
                catch (System.Exception e)
                {
                    WriteStartupDiagnostic("Compatibility patch failed: " + patch.GetType().FullName + " | " + e);
                    try { LogManager.Log.NotifyBad(e); } catch { }
                }
            }
            try
            {
                _extender = UIExtender.Create(ModuleName);
                _extender.Register(typeof(Main).Assembly);
                _extender.Enable();
            }
            catch (System.Exception e)
            {
                WriteStartupDiagnostic("UIExtender initialization failed: " + e);
                try { LogManager.Log.NotifyBad(e); } catch { }
            }
            if (!_afterMenuPatchesApplied)
            {
                _afterMenuPatchesApplied = true;
                foreach (var patch in HarmonyCompatPatches)
                {
                    try { patch.PatchAfterMenus(Harmony); }
                    catch (System.Exception e)
                    {
                        WriteStartupDiagnostic("Compatibility after-menu patch failed: " + patch.GetType().FullName + " | " + e);
                    }
                }
            }
            WriteStartupDiagnostic("Runtime initialization: complete");
        }

        private static void WriteStartupDiagnostic(string message)
        {
            try
            {
                string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Mount and Blade II Bannerlord");
                string dir = Path.Combine(userDir, "Configs", Name);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup.log"), DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch { }
        }'''
s = one(s, old_game, new_game, 'game start')
loader = s.index('        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()')
depth = 0; opened = False; loader_end = None
for i in range(loader, len(s)):
    if s[i] == '{': depth += 1; opened = True
    elif s[i] == '}':
        depth -= 1
        if opened and depth == 0: loader_end = i + 1; break
if loader_end is None: raise RuntimeError('compatibility loader end not found')
safe_loader = '''        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (!typeof(ICompatibilityPatch).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract) continue;
                    object? instance = null;
                    try { instance = type.CreateInstance(); }
                    catch (Exception e) { WriteStartupDiagnostic("Compatibility type creation failed: " + type.FullName + " | " + e); }
                    if (instance is ICompatibilityPatch patch) yield return patch;
                }
            }
        }'''
s = s[:loader] + safe_loader + s[loader_end:]
save('Main.cs', s)

s = load('Descriptors/PlayerSettlementItemTemplate.cs')
s = one(s, '        public string Id;\n\n        public string Culture;',
'''        public string Id;

        public string PrefabId;

        public string Culture;''', 'template PrefabId')
save('Descriptors/PlayerSettlementItemTemplate.cs', s)

s = load('Behaviours/PlayerSettlementBehaviour.cs')
s = one(s, '''        const string GhostGatePrefabId = GhostGateEntityId;
        const string GhostPortEntityId = GhostGateEntityId;
        const string GhostPortPrefabId = GhostGateEntityId;''',
'''        const string GhostGatePrefabId = "map_icon_full_vlandia_village";
        const string GhostPortEntityId = GhostGateEntityId;
        const string GhostPortPrefabId = GhostGatePrefabId;''', 'native ghost visuals')
for typ in ('Castle', 'Town'):
    old = f'''                    templates.Add(new PlayerSettlementItemTemplate
                    {{
                        Id = id,
                        ItemXML = node,
                        Type = (int) SettlementType.{typ},
                        Culture = cst.CultureId
                    }});'''
    new = f'''                    templates.Add(new PlayerSettlementItemTemplate
                    {{
                        Id = id,
                        PrefabId = node.Attributes?["prefab_id"]?.Value ?? id,
                        ItemXML = node,
                        Type = (int) SettlementType.{typ},
                        Culture = cst.CultureId
                    }});'''
    s = one(s, old, new, typ + ' visual id')
s = one(s, '''                    templates.Add(new PlayerSettlementItemTemplate
                    {
                        Id = id,
                        ItemXML = node,
                        Type = (int) SettlementType.Village,
                        Culture = cst.CultureId
                    });''',
'''                    var prefabId = (node.Attributes?["prefab_id"]?.Value ?? id)
                        .Replace("{{OWNER_TYPE}}", forCastle ? "castle" : "town");
                    templates.Add(new PlayerSettlementItemTemplate
                    {
                        Id = id,
                        PrefabId = prefabId,
                        ItemXML = node,
                        Type = (int) SettlementType.Village,
                        Culture = cst.CultureId
                    });''', 'Village visual id')
s = one(s, 'overwriteItem.PrefabId = item.Id;', 'overwriteItem.PrefabId = item.PrefabId ?? item.Id;', 'overwrite visual')
s = one(s, 'target.PrefabId = item.Id;', 'target.PrefabId = item.PrefabId ?? item.Id;', 'rebuild visual')
s = one(s, 'PrefabId = item.Id\n            };', 'PrefabId = item.PrefabId ?? item.Id\n            };', 'new visuals', 3)
s = one(s, 'var curModelIdx = availableModels?.FindIndex(a => a.Id == curModelPrefab) ?? -1;',
'var curModelIdx = availableModels?.FindIndex(a => a.Id == curModelPrefab || (a.PrefabId ?? a.Id) == curModelPrefab) ?? -1;', 'model lookup')
s = one(s, 'string prefabId = template.Id;\n                string entityId = GhostSettlementEntityId;',
'string prefabId = template.PrefabId ?? template.Id;\n                string entityId = GhostSettlementEntityId;', 'placement visual')
save('Behaviours/PlayerSettlementBehaviour.cs', s)
print('Player Settlement native-visual patch applied.')