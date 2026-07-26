from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding='utf-8-sig')
old = '''        static MethodInfo GetFrameAndVisualOfEngines = AccessTools.Property(typeof(MapScreen), "FrameAndVisualOfEngines").GetMethod;
        public static Dictionary<UIntPtr, Tuple<MatrixFrame, SettlementVisual>> FrameAndVisualOfEngines()
        {
            return (Dictionary<UIntPtr, Tuple<MatrixFrame, SettlementVisual>>) GetFrameAndVisualOfEngines.Invoke(null, null);
        }
'''
new = '''        static readonly MethodInfo GetFrameAndVisualOfEngines = AccessTools.Property(typeof(MapScreen), "FrameAndVisualOfEngines")?.GetGetMethod(true);
        public static Dictionary<UIntPtr, Tuple<MatrixFrame, SettlementVisual>> FrameAndVisualOfEngines()
        {
            return GetFrameAndVisualOfEngines?.Invoke(null, null) as Dictionary<UIntPtr, Tuple<MatrixFrame, SettlementVisual>>;
        }
'''
if source.count(old) != 1:
    raise RuntimeError(f'FrameAndVisualOfEngines marker count: {source.count(old)}')
source = source.replace(old, new, 1)
path.write_text(source, encoding='utf-8', newline='\n')
