from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

old_field = "        private static List<ICompatibilityPatch> HarmonyCompatPatches = LoadCompatPatches().ToList();"
new_field = """        private static readonly List<ICompatibilityPatch> HarmonyCompatPatches = new();
        private bool _runtimeInitialized;
        private bool _afterMenuPatchesApplied;"""
if old_field not in source:
    raise RuntimeError("Compatibility patch static initializer not found")
source = source.replace(old_field, new_field, 1)

ctor_marker = """            //Ctor
            Submodule = this;

            //Debugger.Launch();"""
ctor_replacement = """            //Ctor
            Submodule = this;
            WriteStartupDiagnostic("Main constructor");

            //Debugger.Launch();"""
if ctor_marker not in source:
    raise RuntimeError("Main constructor marker not found")
source = source.replace(ctor_marker, ctor_replacement, 1)

submodule_start = source.index("        protected override void OnSubModuleLoad()")
submodule_end = source.index("        protected override void OnBeforeInitialModuleScreenSetAsRoot()", submodule_start)
minimal_submodule = """        protected override void OnSubModuleLoad()
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

"""
source = source[:submodule_start] + minimal_submodule + source[submodule_end:]

old_before_signature = "        protected override void OnBeforeInitialModuleScreenSetAsRoot()"
if old_before_signature not in source:
    raise RuntimeError("OnBeforeInitialModuleScreenSetAsRoot not found")
source = source.replace(old_before_signature, "        private void InitializeMenuDependentState()", 1)

insert_before = "        private void InitializeMenuDependentState()"
minimal_before = """        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            WriteStartupDiagnostic("OnBeforeInitialModuleScreenSetAsRoot");
        }

"""
source = source.replace(insert_before, minimal_before + insert_before, 1)

compat_after_menu = """                    foreach (var patch in HarmonyCompatPatches)
                    {
                        patch.PatchAfterMenus(Harmony!);
                    }

                    CultureTemplates = GatherTemplates();"""
if compat_after_menu not in source:
    raise RuntimeError("Early compatibility after-menu block not found")
source = source.replace(compat_after_menu, "                    CultureTemplates = GatherTemplates();", 1)

old_game_start = """        protected override void OnGameStart(Game game, IGameStarter starterObject)
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
        }"""
new_game_start = """        protected override void OnGameStart(Game game, IGameStarter starterObject)
        {
            try
            {
                WriteStartupDiagnostic("OnGameStart: enter");
                base.OnGameStart(game, starterObject);

                if (game.GameType is Campaign)
                {
                    InitializeMenuDependentState();
                    InitializeRuntimePatches();
                    var initializer = (CampaignGameStarter) starterObject;
                    AddBehaviors(initializer);
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
            if (_runtimeInitialized)
            {
                return;
            }

            _runtimeInitialized = true;
            WriteStartupDiagnostic("Runtime initialization: begin");
            Harmony = new Harmony(HarmonyDomain);

            Type[] ownTypes;
            try
            {
                ownTypes = typeof(Main).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                ownTypes = e.Types.Where(type => type != null).Cast<Type>().ToArray();
                WriteStartupDiagnostic("Own assembly partial type load: " + e);
            }

            foreach (var type in ownTypes)
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                {
                    continue;
                }

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
            try
            {
                HarmonyCompatPatches.AddRange(LoadCompatPatches());
            }
            catch (System.Exception e)
            {
                WriteStartupDiagnostic("Compatibility discovery failed: " + e);
            }

            foreach (var patch in HarmonyCompatPatches)
            {
                try
                {
                    WriteStartupDiagnostic("Compatibility patch begin: " + patch.GetType().FullName);
                    patch.PatchSubmoduleLoad(Harmony);
                    WriteStartupDiagnostic("Compatibility patch complete: " + patch.GetType().FullName);
                }
                catch (System.Exception e)
                {
                    WriteStartupDiagnostic("Compatibility patch failed: " + patch.GetType().FullName + " | " + e);
                    try { LogManager.Log.NotifyBad(e); } catch { }
                }
            }

            try
            {
                WriteStartupDiagnostic("UIExtender initialization: begin");
                _extender = UIExtender.Create(ModuleName);
                _extender.Register(typeof(Main).Assembly);
                _extender.Enable();
                WriteStartupDiagnostic("UIExtender initialization: complete");
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
                    try
                    {
                        patch.PatchAfterMenus(Harmony);
                    }
                    catch (System.Exception e)
                    {
                        WriteStartupDiagnostic("Compatibility after-menu patch failed: " + patch.GetType().FullName + " | " + e);
                        try { LogManager.Log.NotifyBad(e); } catch { }
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
                string logDirectory = Path.Combine(userDir, "Configs", Name);
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "startup.log");
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch
            {
            }
        }"""
if old_game_start not in source:
    raise RuntimeError("OnGameStart block not found")
source = source.replace(old_game_start, new_game_start, 1)

loader_start = source.index("        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()")
brace_count = 0
seen_open = False
loader_end = None
for index in range(loader_start, len(source)):
    char = source[index]
    if char == "{":
        brace_count += 1
        seen_open = True
    elif char == "}":
        brace_count -= 1
        if seen_open and brace_count == 0:
            loader_end = index + 1
            break
if loader_end is None:
    raise RuntimeError("Could not locate compatibility loader end")

safe_loader = """        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (typeof(ICompatibilityPatch).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    {
                        object? inst = null;
                        try
                        {
                            inst = type.CreateInstance();
                        }
                        catch (Exception e)
                        {
                            WriteStartupDiagnostic("Compatibility type creation failed: " + type.FullName + " | " + e);
                        }

                        if (inst is ICompatibilityPatch compatibilityPatch)
                        {
                            yield return compatibilityPatch;
                        }
                    }
                }
            }
        }"""
source = source[:loader_start] + safe_loader + source[loader_end:]

path.write_text(source, encoding="utf-8")
